﻿namespace Perla.Lib

open System
open FsToolkit.ErrorHandling
open Types

[<RequireQualifiedAccess>]
module Env =
  open System.Runtime.InteropServices

  let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

  let platformString =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
      "windows"
    else if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
      "linux"
    else if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
      "darwin"
    else if RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) then
      "freebsd"
    else
      failwith "Unsupported OS"

  let archString =
    match RuntimeInformation.OSArchitecture with
    | Architecture.Arm -> "arm"
    | Architecture.Arm64 -> "arm64"
    | Architecture.X64 -> "64"
    | Architecture.X86 -> "32"
    | _ -> failwith "Unsupported Architecture"

[<RequireQualifiedAccess>]
module Json =
  open System.Text.Json
  open System.Text.Json.Serialization

  let private jsonOptions () =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.AllowTrailingCommas <- true
    opts.ReadCommentHandling <- JsonCommentHandling.Skip
    opts.UnknownTypeHandling <- JsonUnknownTypeHandling.JsonElement
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    opts

  let ToBytes value =
    JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions ())

  let FromBytes<'T> (bytes: byte array) =
    JsonSerializer.Deserialize<'T>(ReadOnlySpan bytes, jsonOptions ())

  let ToText value =
    JsonSerializer.Serialize(value, jsonOptions ())

  let ToTextMinified value =
    let opts = jsonOptions ()
    opts.WriteIndented <- false
    JsonSerializer.Serialize(value, opts)

  let ToPackageJson dependencies =
    JsonSerializer.Serialize({| dependencies = dependencies |}, jsonOptions ())

[<RequireQualifiedAccessAttribute>]
module Http =
  open Flurl
  open Flurl.Http

  [<Literal>]
  let SKYPACK_CDN = "https://cdn.skypack.dev"

  [<Literal>]
  let SKYPACK_API = "https://api.skypack.dev/v1"

  [<Literal>]
  let JSPM_API = "https://api.jspm.io/generate"

  let private getSkypackInfo (name: string) (alias: string) =
    taskResult {
      try
        let info = {| lookUp = $"%s{name}" |}
        let! res = $"{SKYPACK_CDN}/{info.lookUp}".GetAsync()

        if res.StatusCode >= 400 then
          return! PackageNotFoundException |> Error

        let mutable pinnedUrl = ""
        let mutable importUrl = ""

        let info =
          if
            res.Headers.TryGetFirst("x-pinned-url", &pinnedUrl)
            |> not
          then
            {| info with pin = None |}
          else
            {| info with pin = Some pinnedUrl |}

        let info =
          if
            res.Headers.TryGetFirst("x-import-url", &importUrl)
            |> not
          then
            {| info with import = None |}
          else
            {| info with import = Some importUrl |}

        return
          [ alias, $"{SKYPACK_CDN}{info.pin |> Option.defaultValue info.lookUp}" ],
          // skypack doesn't handle any import maps so the scopes will always be empty
          []
      with
      | :? Flurl.Http.FlurlHttpException as ex ->
        match ex.StatusCode |> Option.ofNullable with
        | Some code when code >= 400 ->
          return! PackageNotFoundException |> Error
        | _ -> ()

        return! ex :> Exception |> Error
      | ex -> return! ex |> Error
    }

  let private getJspmInfo name alias source =
    taskResult {
      let queryParams =
        {| install = [| $"{name}" |]
           env = "browser"
           provider =
            match source with
            | Source.Skypack -> "skypack"
            | Source.Jspm -> "jspm"
            | Source.Jsdelivr -> "jsdelivr"
            | Source.Unpkg -> "unpkg"
            | _ ->
              printfn
                $"Warn: an unknown provider has been specified: [{source}] defaulting to jspm"

              "jspm" |}

      try
        let! res =
          JSPM_API
            .SetQueryParams(queryParams)
            .GetJsonAsync<JspmResponse>()

        let scopes =
          // F# type serialization hits again!
          // the JSPM response may include a scope object or not
          // so try to safely check if it exists or not
          match res.map.scopes :> obj |> Option.ofObj with
          | None -> Map.empty
          | Some value -> value :?> Map<string, Scope>

        return
          res.map.imports
          |> Map.toList
          |> List.map (fun (k, v) -> alias, v),
          scopes |> Map.toList
      with
      | :? Flurl.Http.FlurlHttpException as ex ->
        match ex.StatusCode |> Option.ofNullable with
        | Some code when code >= 400 ->
          return! PackageNotFoundException |> Error
        | _ -> ()

        return! ex :> Exception |> Error
    }

  let getPackageUrlInfo (name: string) (alias: string) (source: Source) =
    match source with
    | Source.Skypack -> getSkypackInfo name alias
    | _ -> getJspmInfo name alias source

  let searchPackage (name: string) (page: int option) =
    taskResult {
      let page = defaultArg page 1

      let! res =
        SKYPACK_API
          .AppendPathSegment("search")
          .SetQueryParams({| q = name; p = page |})
          .GetJsonAsync<SkypackSearchResponse>()

      return
        {| meta = res.meta
           results = res.results |}
    }

  let showPackage name =
    taskResult {
      let! res =
        $"{SKYPACK_API}/package/{name}"
          .GetJsonAsync<SkypackPackageResponse>()

      return
        {| name = res.name
           versions = res.versions
           distTags = res.distTags
           maintainers = res.maintainers
           license = res.license
           updatedAt = res.updatedAt
           registry = res.registry
           description = res.description
           isDeprecated = res.isDeprecated
           dependenciesCount = res.dependenciesCount
           links = res.links |}
    }

[<RequireQualifiedAccess>]
module Fs =
  open System.IO
  open FSharp.Control.Reactive

  type ChangeKind =
    | Created
    | Deleted
    | Renamed
    | Changed

  type FileChangedEvent =
    { oldName: string option
      ChangeType: ChangeKind
      path: string
      name: string }

  type IFileWatcher =
    inherit IDisposable
    abstract member FileChanged: IObservable<FileChangedEvent>


  ///<summary>
  /// Gets the base templates directory (next to the perla binary)
  /// and appends the final path repository name
  /// </summary>
  let getPerlaRepositoryPath (repositoryName: string) (branch: string) =
    Path.Combine(Path.TemplatesDirectory, $"{repositoryName}-{branch}")
    |> Path.GetFullPath

  let getPerlaTemplatePath
    (repo: PerlaTemplateRepository)
    (child: string option)
    =
    match child with
    | Some child -> Path.Combine(repo.path, child)
    | None -> repo.path
    |> Path.GetFullPath

  let getPerlaTemplateTarget projectName =
    Path.Combine("./", projectName)
    |> Path.GetFullPath

  let removePerlaRepository (repository: PerlaTemplateRepository) =
    Directory.Delete(repository.path, true)

  let getPerlaTemplateScriptContent templatePath clamRepoPath =
    let readTemplateScript =
      try
        File.ReadAllText(Path.Combine(templatePath, "templating.fsx"))
        |> Some
      with
      | _ -> None

    let readRepoScript () =
      try
        File.ReadAllText(Path.Combine(clamRepoPath, "templating.fsx"))
        |> Some
      with
      | _ -> None

    readTemplateScript
    |> Option.orElseWith (fun () -> readRepoScript ())

  let getPerlaRepositoryChildren (repo: PerlaTemplateRepository) =
    DirectoryInfo(repo.path).GetDirectories()

  let getPerlaConfig filepath =
    try
      let bytes = File.ReadAllBytes filepath
      Json.FromBytes<PerlaConfig> bytes |> Ok
    with
    | ex -> ex |> Error

  let getProxyConfig filepath =
    try
      let bytes = File.ReadAllBytes filepath
      Json.FromBytes<Map<string, string>> bytes |> Some
    with
    | ex -> None

  let ensureParentDirectory path =
    try
      Directory.CreateDirectory(path) |> ignore |> Ok
    with
    | ex -> ex |> Error

  let createPerlaConfig path config =
    let serialized = Json.ToBytes config

    try
      File.WriteAllBytes(path, serialized) |> Ok
    with
    | ex -> Error ex

  let getOrCreateLockFile configPath =
    taskResult {
      try
        let path = Path.GetFullPath($"%s{configPath}.importmap")

        do! ensureParentDirectory (Path.GetDirectoryName(path))

        let bytes = File.ReadAllBytes(path)

        return Json.FromBytes<PackagesLock> bytes
      with
      | :? System.IO.FileNotFoundException ->
        return
          { imports = Map.empty
            scopes = Map.empty }
      | ex -> return! ex |> Error
    }

  let writeLockFile configPath (fdsLock: PackagesLock) =
    let path = Path.GetFullPath($"%s{configPath}.importmap")
    let serialized = Json.ToBytes fdsLock

    try
      File.WriteAllBytes(path, serialized) |> Ok
    with
    | ex -> Error ex



  let getOrCreateImportMap path =
    taskResult {
      try
        let path = Path.GetFullPath(path)

        do! ensureParentDirectory (Path.GetDirectoryName(path))

        let bytes = File.ReadAllBytes(path)

        return Json.FromBytes<ImportMap> bytes
      with
      | :? System.IO.FileNotFoundException ->
        return
          { imports = Map.empty
            scopes = Map.empty }
      | ex -> return! ex |> Error
    }

  let writeImportMap path importMap =
    result {
      let bytes = Json.ToBytes importMap

      try
        let path = Path.GetFullPath(path)

        Directory.CreateDirectory(Path.GetDirectoryName(path))
        |> ignore

        File.WriteAllBytes(path, bytes)
      with
      | ex -> return! ex |> Error
    }

  let CompileErrWatcherEvent = lazy (Event<string>())

  let PublishCompileErr err =
    CompileErrWatcherEvent.Value.Trigger err

  let compileErrWatcher () = CompileErrWatcherEvent.Value.Publish

  let getFileWatcher (config: WatchConfig) =
    let watchers =
      (defaultArg config.directories ([ "./src" ] |> Seq.ofList))
      |> Seq.map (fun dir ->
        let fsw = new FileSystemWatcher(dir)
        fsw.IncludeSubdirectories <- true
        fsw.NotifyFilter <- NotifyFilters.FileName ||| NotifyFilters.Size

        let filters =
          defaultArg
            config.extensions
            (Seq.ofList [ "*.js"
                          "*.css"
                          "*.ts"
                          "*.tsx"
                          "*.jsx"
                          "*.json" ])

        for filter in filters do
          fsw.Filters.Add(filter)

        fsw.EnableRaisingEvents <- true
        fsw)


    let subs =
      watchers
      |> Seq.map (fun watcher ->
        [ watcher.Renamed
          |> Observable.throttle (TimeSpan.FromMilliseconds(400.))
          |> Observable.map (fun e ->
            { oldName = Some e.OldName
              ChangeType = Renamed
              name = e.Name
              path = e.FullPath })
          watcher.Changed
          |> Observable.throttle (TimeSpan.FromMilliseconds(400.))
          |> Observable.map (fun e ->
            { oldName = None
              ChangeType = Changed
              name = e.Name
              path = e.FullPath })
          watcher.Deleted
          |> Observable.throttle (TimeSpan.FromMilliseconds(400.))
          |> Observable.map (fun e ->
            { oldName = None
              ChangeType = Deleted
              name = e.Name
              path = e.FullPath })
          watcher.Created
          |> Observable.throttle (TimeSpan.FromMilliseconds(400.))
          |> Observable.map (fun e ->
            { oldName = None
              ChangeType = Created
              name = e.Name
              path = e.FullPath }) ]
        |> Observable.mergeSeq)

    { new IFileWatcher with
        override _.Dispose() : unit =
          watchers
          |> Seq.iter (fun watcher -> watcher.Dispose())

        override _.FileChanged: IObservable<FileChangedEvent> =
          Observable.mergeSeq subs }

  let private tryReadFileWithExtension file ext =
    taskResult {
      try
        match ext with
        | Typescript ->
          let! content = File.ReadAllTextAsync($"{file}.ts")
          return (content, Typescript)
        | Jsx ->
          let! content = File.ReadAllTextAsync($"{file}.jsx")
          return (content, Jsx)
        | Tsx ->
          let! content = File.ReadAllTextAsync($"{file}.tsx")
          return (content, Tsx)
      with
      | ex -> return! ex |> Error
    }

  let tryReadFile (filepath: string) =
    let fileNoExt =
      Path.Combine(
        Path.GetDirectoryName(filepath),
        Path.GetFileNameWithoutExtension(filepath)
      )

    tryReadFileWithExtension fileNoExt Typescript
    |> TaskResult.orElseWith (fun _ -> tryReadFileWithExtension fileNoExt Jsx)
    |> TaskResult.orElseWith (fun _ -> tryReadFileWithExtension fileNoExt Tsx)

  let tryGetTsconfigFile () =
    try
      File.ReadAllText("./tsconfig.json") |> Some
    with
    | _ -> None

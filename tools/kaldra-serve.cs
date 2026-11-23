// Serves a published Kaldra browser app locally, as a host would, for trying it in a browser.
// Usage: dotnet run --file tools/kaldra-serve.cs -- <publish dir or its wwwroot> [--port 5080] [--no-isolation]
//
// - Cross-origin isolation (COOP same-origin, COEP require-corp) by default: a multithreaded WebAssembly build
//   (WasmEnableThreads) needs SharedArrayBuffer, which browsers only give an isolated page. --no-isolation leaves the
//   headers off, to see how a build behaves on a host that doesn't send them.
// - The types the .NET runtime's files need, whatever the machine's registry says: a module worker
//   (dotnet.native.worker.*.mjs) served as text/plain, as Python's server does on Windows, won't start, and the
//   threaded runtime then hangs at "loading .NET runtime".
// - The publish's precompressed .br or .gz beside a file, when the browser takes it.
// - Nothing cached: a rebuilt asset whose URL isn't fingerprinted (the Kaldra packages' .js) is fetched again.

#:sdk Microsoft.NET.Sdk.Web

using Microsoft.AspNetCore.StaticFiles;

string? dir = null;
int port = 5080;
bool isolated = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--no-isolation": isolated = false; break;
        default: dir = args[i]; break;
    }
}
if (dir is null)
{
    Console.Error.WriteLine("Usage: dotnet run --file tools/kaldra-serve.cs -- <publish dir or its wwwroot> [--port 5080] [--no-isolation]");
    return 1;
}
// A publish folder holds wwwroot/; the page is in there
string root = Path.GetFullPath(File.Exists(Path.Combine(dir, "wwwroot", "index.html")) ? Path.Combine(dir, "wwwroot") : dir);
if (!File.Exists(Path.Combine(root, "index.html")))
{
    Console.Error.WriteLine($"No index.html in {root}: is this a published browser app (dotnet publish -r browser-wasm)?");
    return 1;
}
// A request is served only from inside root: compared against root with its separator, so a sibling "<root>2" isn't
// inside it, and ignoring case only on Windows, whose file system does (elsewhere a differently cased path 404s)
string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

var types = new FileExtensionContentTypeProvider();
types.Mappings[".mjs"] = "text/javascript";
types.Mappings[".js"] = "text/javascript";
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";
types.Mappings[".wgsl"] = "text/plain";
types.Mappings[".pdb"] = "application/octet-stream";

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.Run(async context =>
{
    var request = context.Request;
    var response = context.Response;
    response.Headers.CacheControl = "no-store";
    if (isolated)
    {
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    }

    string relative = Uri.UnescapeDataString(request.Path.Value ?? "/").TrimStart('/');
    if (relative.Length == 0 || relative.EndsWith('/')) relative += "index.html";
    string path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(rootPrefix, pathComparison) || !File.Exists(path))
    {
        response.StatusCode = 404;
        Console.WriteLine($"404 {request.Path}");
        return;
    }

    response.ContentType = types.TryGetContentType(path, out var type) ? type : "application/octet-stream";
    response.Headers.Vary = "Accept-Encoding";
    string accepts = request.Headers.AcceptEncoding.ToString();
    foreach (var (encoding, extension) in new[] { ("br", ".br"), ("gzip", ".gz") })
    {
        if (accepts.Contains(encoding, StringComparison.OrdinalIgnoreCase) && File.Exists(path + extension))
        {
            response.Headers.ContentEncoding = encoding;
            await response.SendFileAsync(path + extension);
            return;
        }
    }
    await response.SendFileAsync(path);
});

Console.WriteLine($"Serving {root}");
Console.WriteLine($"  http://127.0.0.1:{port}/   cross-origin isolation {(isolated ? "on" : "off")}");
await app.RunAsync();
return 0;

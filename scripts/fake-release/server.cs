// Fake GitHub release feed for testing `resetme update` without publishing a release.
//   dotnet run scripts/fake-release/server.cs -- <asset-dir> <port> <tag>
// GET /releases/latest  -> GitHub-shaped JSON listing every file in <asset-dir> as an asset
// GET /<file>           -> the file itself
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

var directory = Path.GetFullPath(args[0]);
var port = args[1];
var tag = args[2];
var prefix = $"http://localhost:{port}/";

var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
Console.WriteLine($"fake release feed {tag} on {prefix}releases/latest");

while (true)
{
    var context = await listener.GetContextAsync();
    var path = context.Request.Url!.AbsolutePath.TrimStart('/');
    try
    {
        if (path == "releases/latest")
        {
            var assets = new JsonArray();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                assets.Add((JsonNode)new JsonObject
                {
                    ["name"] = name,
                    ["browser_download_url"] = prefix + Uri.EscapeDataString(name),
                    ["size"] = new FileInfo(file).Length,
                });
            }

            var json = new JsonObject
            {
                ["tag_name"] = tag,
                ["html_url"] = $"{prefix}releases/{tag}",
                ["assets"] = assets,
            };
            await Write(context.Response, 200, "application/json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        else if (File.Exists(Path.Combine(directory, Uri.UnescapeDataString(path))))
        {
            await Write(context.Response, 200, "application/octet-stream", await File.ReadAllBytesAsync(Path.Combine(directory, Uri.UnescapeDataString(path))));
        }
        else
        {
            await Write(context.Response, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
}

static async Task Write(HttpListenerResponse response, int status, string type, byte[] body)
{
    response.StatusCode = status;
    response.ContentType = type;
    response.ContentLength64 = body.Length;
    await response.OutputStream.WriteAsync(body);
    response.Close();
}

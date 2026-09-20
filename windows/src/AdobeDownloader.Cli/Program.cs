using System.Globalization;
using System.Text.Json;
using AdobeDownloader.Core;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("""
            Adobe Downloader — Windows phase-one CLI (downloads only)
            catalog [--product KBRG] [--platform win64] [--channel ccm] [--json]
            manifest --product KBRG --version 16.0.7.36 [--locale en_US] [--out manifest.json]
            download --product CODE --version EXACT --package NAME --out DIRECTORY
                     [--max-mib 100] [--sha256 EXPECTED] [--platform win64] [--channel sti]

            Versions must match ProductVersion exactly. Default platform: win64; channel: ccm.
            No dependency resolution or installation yet. Download selects ONE full package.
            SHA-256 is a local receipt unless --sha256 supplies an independently trusted digest.
            Ctrl+C cancels and removes the incomplete file. Existing files are never overwritten.
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var command = args[0];
        if (command is not ("catalog" or "manifest" or "download")) throw new ArgumentException("Unknown command; use --help.");
        var options = ParseOptions(args[1..]);
        var allowed = command switch
        {
            "catalog" => new[] { "product", "platform", "channel", "json" },
            "manifest" => ["product", "version", "platform", "channel", "locale", "out"],
            _ => ["product", "version", "platform", "channel", "locale", "out", "package", "max-mib", "sha256"]
        };
        if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Option is not valid for this command; use --help.");
        string Get(string name, string fallback) => options.GetValueOrDefault(name) ?? fallback;
        string Require(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");
        var platform = Get("platform", "win64");
        var channel = Get("channel", "ccm");
        if (channel is not ("ccm" or "sti" or "nocc")) throw new ArgumentException("Channel must be ccm, sti, or nocc.");
        var product = command == "catalog" ? Get("product", "") : Require("product");
        var version = command == "catalog" ? "" : Require("version");
        var packageName = command == "download" ? Require("package") : "";
        var output = command == "download" ? Require("out") : Get("out", "");
        if (command == "manifest" && output.Length > 0 && File.Exists(output)) throw new IOException("Manifest output already exists.");
        if (!long.TryParse(Get("max-mib", "100"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxMiB) || maxMiB <= 0)
            throw new ArgumentException("--max-mib must be a positive integer.");
        var maxBytes = checked(maxMiB * 1024 * 1024);
        using var http = AdobeTransport.CreateHttpClient();
        var transport = new AdobeTransport(http);
        var catalog = await new CatalogClient(transport).FetchAsync(platform, cancellation.Token);
        var matches = catalog.Where(x => x.Channel == channel && (product.Length == 0 ||
            x.SapCode.Equals(product, StringComparison.OrdinalIgnoreCase))).ToArray();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        if (command == "catalog")
        {
            if (options.ContainsKey("json")) Console.WriteLine(JsonSerializer.Serialize(matches, jsonOptions));
            else
            {
                Console.WriteLine("CODE\tPRODUCT VERSION\tPLATFORM\tLANGUAGE SET\tPACKAGE TYPE\tNAME");
                foreach (var build in matches.OrderBy(x => x.SapCode).ThenBy(x => x.ProductVersion))
                    Console.WriteLine($"{build.SapCode}\t{(build.ProductVersion.Length > 0 ? build.ProductVersion : "legacy:" + build.Version)}\t{build.Platform}\t{build.LanguageSet}\t{build.PackageType}\t{build.DisplayName}");
                Console.Error.WriteLine($"{matches.Length} builds from Adobe ({platform}, {channel}).");
            }
            return 0;
        }
        var locale = Get("locale", "en_US");
        matches = matches.Where(x => x.ProductVersion == version &&
            (x.Locales.Count == 0 || x.Locales.Contains(locale) || x.Locales.Contains("ALL") || x.Locales.Contains("mul"))).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Expected one build, found {matches.Length}. Check exact version, channel, platform, and locale using catalog --json.");
        var manifest = await new ManifestClient(transport).FetchAsync(matches[0], cancellation.Token);
        if (command == "manifest")
        {
            if (output.Length > 0)
            {
                await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(manifest.RawJson.AsMemory(), cancellation.Token);
            }
            Console.WriteLine(JsonSerializer.Serialize(manifest with { RawJson = "" }, jsonOptions));
            return 0;
        }
        var package = manifest.Packages.SingleOrDefault(x => x.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Package not found; inspect manifest output for exact package names.");
        Console.Error.WriteLine($"Downloading {package.FileName} ({package.DownloadSize:N0} bytes). This is one package, not a complete installation set.");
        var result = await new PackageDownloader(transport).DownloadAsync(package, output, maxBytes,
            options.GetValueOrDefault("sha256"), ct: cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine(cancellation.IsCancellationRequested ? "Cancelled." : "Adobe request or download timed out.");
        return cancellation.IsCancellationRequested ? 130 : 1;
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or NotSupportedException or HttpRequestException or
        JsonException or System.Xml.XmlException or OverflowException)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected a --named option.");
        var key = args[i][2..];
        string value;
        if (key == "json") value = "true";
        else
        {
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"--{key} needs a value.");
            value = args[i];
        }
        if (!result.TryAdd(key, value)) throw new ArgumentException($"Duplicate option --{key}.");
    }
    return result;
}

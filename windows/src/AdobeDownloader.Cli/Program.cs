using System.Globalization;
using System.Text.Json;
using AdobeDownloader.Core;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("""
            Flash Creative — Windows download CLI (phase two in progress)
            catalog [--product KBRG] [--platform win64] [--channel ccm] [--json]
            manifest --product KBRG --version 16.0.7.36 [--locale en_US] [--out manifest.json]
            download --product CODE --version EXACT --package NAME --out DIRECTORY
                     [--max-mib 100] [--sha256 EXPECTED] [--platform win64] [--channel sti]
            plan --product CODE --version EXACT --out plan.json [--locale en_US] [--os-version 10.0.22000]
                 [--platform win64] [--channel ccm] [--modules ID,SAP:ID] [--features NAME,SAP:NAME]
                 [--deployment individual|enterprise]
            inspect-delta --plan plan.json --product CODE --package NAME --base-version EXACT [--archive delta.zip] [--out report.json]
            queue-create --plan plan.json --out QUEUE_DIRECTORY
            queue-run --queue QUEUE_DIRECTORY [--max-mib 100]
            queue-status --queue QUEUE_DIRECTORY
            verify-signature --file EXECUTABLE --publisher "Microsoft Corporation"

            Versions must match ProductVersion exactly. Default platform: win64; channel: ccm.
            download selects ONE package; plan resolves supported dependencies into full packages.
            queue-run persists progress and resumes using strong ETags. No installation yet.
            Adobe TYPE2 metadata verifies package segments when available; output reports the verification method.
            Optional modules require explicit --modules selection, including consent-requiring modules.
            Ctrl+C preserves queue partials; the single-package download command removes them.
            Existing completed files are never overwritten.
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var command = args[0];
        if (command is not ("catalog" or "manifest" or "download" or "plan" or "queue-create" or "queue-run" or "queue-status" or "verify-signature" or "inspect-delta"))
            throw new ArgumentException("Unknown command; use --help.");
        var options = ParseOptions(args[1..]);
        if (command == "inspect-delta") return await InspectDeltaCommandAsync(options, cancellation.Token);
        if (command == "verify-signature")
        {
            if (options.Count != 2 || !options.ContainsKey("file") || !options.ContainsKey("publisher"))
                throw new ArgumentException("verify-signature requires --file and --publisher only.");
            Console.WriteLine(JsonSerializer.Serialize(WindowsSignatureVerifier.Verify(options["file"], options["publisher"]), JsonFiles.Options));
            return 0;
        }
        if (command.StartsWith("queue-", StringComparison.Ordinal)) return await QueueCommandAsync(command, options, cancellation.Token);
        var allowed = command switch
        {
            "catalog" => new[] { "product", "platform", "channel", "json" },
            "manifest" => ["product", "version", "platform", "channel", "locale", "out"],
            "plan" => ["product", "version", "platform", "channel", "locale", "os-version", "out", "modules", "features", "deployment"],
            _ => ["product", "version", "platform", "channel", "locale", "out", "package", "max-mib", "sha256"]
        };
        if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Option is not valid for this command; use --help.");
        string Get(string name, string fallback) => options.GetValueOrDefault(name) ?? fallback;
        string Require(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");
        var deployment = Get("deployment", "individual");
        if (deployment is not ("individual" or "enterprise")) throw new ArgumentException("--deployment must be individual or enterprise.");
        var platform = Get("platform", "win64");
        var channel = Get("channel", "ccm");
        if (channel is not ("ccm" or "sti" or "nocc")) throw new ArgumentException("Channel must be ccm, sti, or nocc.");
        var product = command == "catalog" ? Get("product", "") : Require("product");
        var version = command == "catalog" ? "" : Require("version");
        var packageName = command == "download" ? Require("package") : "";
        var output = command is "download" or "plan" ? Require("out") : Get("out", "");
        if (command is "manifest" or "plan" && output.Length > 0 && File.Exists(output)) throw new IOException("Output already exists.");
        if (!long.TryParse(Get("max-mib", "100"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxMiB) || maxMiB <= 0)
            throw new ArgumentException("--max-mib must be a positive integer.");
        var maxBytes = checked(maxMiB * 1024 * 1024);
        using var http = AdobeTransport.CreateHttpClient();
        var transport = new AdobeTransport(http);
        var catalog = command == "plan"
            ? await new CatalogClient(transport).FetchDependencyCatalogAsync(platform, cancellation.Token)
            : await new CatalogClient(transport).FetchAsync(platform, cancellation.Token);
        var matches = catalog.Where(x => x.Channel == channel && x.Platform == platform && (product.Length == 0 ||
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
        if (command == "plan")
        {
            var planner = new DownloadPlanner(new ManifestClient(transport).FetchAsync);
            var plan = await planner.CreateAsync(matches[0], catalog, locale,
                Get("os-version", Environment.OSVersion.Version.ToString()), cancellation.Token,
                new SelectionOptions(options.GetValueOrDefault("modules")?.Split(','), options.GetValueOrDefault("features")?.Split(','), deployment == "enterprise"));
            plan = plan with { RequiresAdobeValidation = true };
            DownloadQueue.Validate(plan);
            await JsonFiles.WriteAsync(output, plan, overwrite: false, ct: cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Path = Path.GetFullPath(output), Products = plan.Products.Select(x => new { x.Build.SapCode, x.Build.ProductVersion, x.Build.Platform }),
                Packages = plan.Downloads.Count, plan.TotalBytes, plan.IsEnterpriseDeployment,
                Note = "Full packages; delta fallback reasons are saved. Queue downloads require Adobe HTTPS SHA-256 segment verification. No installation or detached Adobe signature verification."
            }, JsonFiles.Options));
            return 0;
        }
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
        if (string.IsNullOrWhiteSpace(package.ValidationUrl)) throw new InvalidDataException("This package has no Adobe validation URL; download refused.");
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
    catch (Exception ex) when (ex is ArgumentException or FormatException or UnauthorizedAccessException or IOException or InvalidDataException or NotSupportedException or HttpRequestException or
        JsonException or System.Xml.XmlException or OverflowException or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

static async Task<int> InspectDeltaCommandAsync(Dictionary<string, string> options, CancellationToken ct)
{
    string Require(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");
    string[] allowed = ["plan", "product", "package", "base-version", "archive", "out"];
    if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Invalid inspect-delta option; use --help.");
    var plan = await JsonFiles.ReadAsync<DownloadPlan>(Require("plan"), ct);
    using var http = AdobeTransport.CreateHttpClient();
    var report = await new DeltaInspector(new AdobeTransport(http)).InspectAsync(plan,
        Require("product"), Require("package"), Require("base-version"), ct, options.GetValueOrDefault("archive"));
    if (options.TryGetValue("out", out var output)) await JsonFiles.WriteAsync(output, report, overwrite: false, ct: ct);
    Console.WriteLine(JsonSerializer.Serialize(report, JsonFiles.Options));
    return 0;
}

static async Task<int> QueueCommandAsync(string command, Dictionary<string, string> options, CancellationToken ct)
{
    var allowed = command switch
    {
        "queue-create" => new[] { "plan", "out" }, "queue-run" => ["queue", "max-mib"], _ => ["queue"]
    };
    if (options.Keys.Any(x => !allowed.Contains(x))) throw new ArgumentException("Invalid queue option; use --help.");
    string Require(string name) => options.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required.");
    if (command == "queue-create")
    {
        var plan = await JsonFiles.ReadAsync<DownloadPlan>(Require("plan"), ct);
        await DownloadQueue.CreateAsync(plan, Require("out"), ct);
        Console.WriteLine($"Queue created: {Path.GetFullPath(Require("out"))} ({plan.Downloads.Count} packages, {plan.TotalBytes:N0} bytes).");
        return 0;
    }
    var directory = Require("queue");
    QueueSnapshot snapshot;
    if (command == "queue-run")
    {
        if (!long.TryParse(options.GetValueOrDefault("max-mib", "100"), NumberStyles.None, CultureInfo.InvariantCulture, out var mib) || mib <= 0)
            throw new ArgumentException("--max-mib must be a positive integer.");
        using var http = AdobeTransport.CreateHttpClient();
        snapshot = await new DownloadQueue(new ResumableDownloader(new AdobeTransport(http)))
            .RunAsync(directory, checked(mib * 1024 * 1024), ct);
    }
    else snapshot = await DownloadQueue.ReadAsync(directory, ct);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        snapshot.Plan.TotalBytes,
        Items = snapshot.Items.Select(x => new { x.Download.SapCode, Package = x.Download.Package.Name, x.Status, x.Sha256, x.Verification, x.Error })
    }, JsonFiles.Options));
    return 0;
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

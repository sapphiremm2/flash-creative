using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public sealed record InstallationInput
{
    public required string Product { get; init; }
    public required string Package { get; init; }
    public required long Bytes { get; init; }
}
public sealed record InstallationRequest
{
    public required int Version { get; init; }
    public required Guid RequestId { get; init; }
    public required string Product { get; init; }
    public required string ProductVersion { get; init; }
    public required Guid BuildId { get; init; }
    public required string Platform { get; init; }
    public required string Locale { get; init; }
    public required bool Enterprise { get; init; }
    public required string[] Modules { get; init; }
    public required InstallationInput[] Inputs { get; init; }
}

/// <summary>Bounded selection-only wire format for the future helper. Parsing is NOT authorization.
/// No filesystem paths, claimed user identities, operations, publishers, hashes, or command lines.
/// Peer authentication, replay prevention, archive transport and independent plan verification remain required.</summary>
public static class InstallationRequestCodec
{
    private static readonly JsonSerializerOptions Options = new() {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public static InstallationRequest Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 2 or > 65536) throw new InvalidDataException("Installation request size is invalid.");
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(json.RootElement);
            var request = JsonSerializer.Deserialize<InstallationRequest>(bytes, Options) ?? throw new InvalidDataException("Installation request is empty.");
            Validate(request); return request;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid installation request JSON.", ex); }
    }
    public static byte[] Write(InstallationRequest request)
    {
        Validate(request); var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Options);
        if (bytes.Length > 65536) throw new InvalidDataException("Installation request exceeds its size limit.");
        return bytes;
    }
    private static void Validate(InstallationRequest request)
    {
        if (request.Version != 1 || request.RequestId == Guid.Empty || request.BuildId == Guid.Empty ||
            !Token(request.Product, 32) || !Token(request.ProductVersion, 64) || request.Platform is not ("win64" or "winarm64") ||
            request.Locale is null || !Regex.IsMatch(request.Locale, "^[a-z]{2}_[A-Z]{2}$") || request.Modules is null || request.Modules.Length > 64 ||
            request.Modules.Any(m => !Token(m, 128)) || request.Modules.Distinct(StringComparer.Ordinal).Count() != request.Modules.Length ||
            request.Inputs is null || request.Inputs.Length is < 1 or > 256)
            throw new InvalidDataException("Unsupported or incomplete installation selection.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var input in request.Inputs)
        {
            if (input is null || !Token(input.Product, 32) || !Token(input.Package, 128) || input.Bytes is < 1 or > 64L * 1024 * 1024 * 1024 ||
                !identities.Add(input.Product + ":" + input.Package)) throw new InvalidDataException("Invalid or duplicate archive input.");
            total += input.Bytes;
            if (total > 256L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive input budget exceeded.");
        }
    }
    private static bool Token(string? text, int limit) => text is { Length: > 0 } && text.Length <= limit &&
        Regex.IsMatch(text, "^[A-Za-z0-9][A-Za-z0-9._-]*$");
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate installation request field."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}

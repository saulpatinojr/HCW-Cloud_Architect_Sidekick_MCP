using System.Text.RegularExpressions;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Errors;

namespace AdrMcp.Application.Services;

/// <summary>Deterministic validation of upload requests: filename, extension, media type, size, path traversal.</summary>
public static partial class FileValidation
{
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ValidationException("fileName is required.");
        // Take only the final path segment; reject traversal and control chars.
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name is "" or "." or "..") throw new ValidationException("fileName is invalid.");
        if (name.Any(c => char.IsControl(c))) throw new ValidationException("fileName contains control characters.");
        name = Unsafe().Replace(name, "_");
        if (name.Length > 180) name = name[^180..];
        return name;
    }

    public static string? GetExtension(string safeName)
    {
        var lower = safeName.ToLowerInvariant();
        if (lower.EndsWith(".env.sample")) return ".env.sample";
        var idx = lower.LastIndexOf('.');
        return idx <= 0 ? null : lower[idx..];
    }

    public static void ValidateRequest(string safeName, string mediaType, long sizeBytes, UploadOptions options)
    {
        var errors = new List<string>();
        if (sizeBytes <= 0) errors.Add("expectedSizeBytes must be positive.");
        if (sizeBytes > options.MaxFileBytes) throw new DomainException(ErrorCodes.PayloadTooLarge, $"File exceeds the maximum of {options.MaxFileBytes} bytes.");

        var ext = GetExtension(safeName);
        var extAllowed = ext is not null && options.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        var nameAllowed = options.ExtensionlessAllowedNames.Contains(safeName, StringComparer.OrdinalIgnoreCase);
        if (!extAllowed && !nameAllowed)
            throw new DomainException(ErrorCodes.UnsupportedFileType, $"File type '{ext ?? safeName}' is not allowed.");

        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 120 || !MediaType().IsMatch(mediaType)) errors.Add("mediaType is invalid.");
        else if (!options.AllowedMediaTypePrefixes.Any(p => mediaType.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException(ErrorCodes.UnsupportedFileType, $"Media type '{mediaType}' is not allowed.");

        if (errors.Count > 0) throw new ValidationException(errors);
    }

    /// <summary>Lightweight MIME sniffing: verifies the declared type is plausible for the first bytes. Not a substitute for scanning.</summary>
    public static (bool Plausible, string Detected) SniffMediaType(ReadOnlySpan<byte> head, string declared)
    {
        string detected = "application/octet-stream";
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04) detected = "application/zip";
        else if (head.Length >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46) detected = "application/pdf";
        else if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) detected = "image/png";
        else if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) detected = "image/jpeg";
        else if (head.Length >= 4 && (head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46)) detected = "application/x-elf";
        else if (head.Length >= 2 && head[0] == 0x4D && head[1] == 0x5A) detected = "application/x-msdownload";
        else if (IsProbablyText(head)) detected = "text/*";

        if (detected is "application/x-elf" or "application/x-msdownload") return (false, detected);
        var plausible = detected switch
        {
            "text/*" => declared.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || declared.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && !declared.Contains("zip") && !declared.Contains("pdf"),
            "application/octet-stream" => true,
            _ => string.Equals(detected, declared, StringComparison.OrdinalIgnoreCase) || declared == "application/octet-stream"
        };
        return (plausible, detected);
    }

    public static bool IsProbablyText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return true;
        int control = 0;
        foreach (var b in bytes)
        {
            if (b == 0) return false;
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) control++;
        }
        return control * 100 / bytes.Length < 5;
    }

    public static bool IsTextMediaType(string mediaType, string safeName)
    {
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        if (mediaType is "application/json" or "application/xml" or "application/x-yaml" or "application/yaml" or "application/toml") return true;
        var ext = GetExtension(safeName);
        return ext is not (".zip" or ".pdf" or ".png" or ".jpg" or ".jpeg");
    }

    [GeneratedRegex(@"[^A-Za-z0-9._\-+ ()\[\]]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"^[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+$", RegexOptions.IgnoreCase)] private static partial Regex MediaType();
}

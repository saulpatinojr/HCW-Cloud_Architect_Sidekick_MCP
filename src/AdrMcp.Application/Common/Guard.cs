using AdrMcp.Domain.Errors;

namespace AdrMcp.Application.Common;

/// <summary>Collects validation errors and throws once. Keeps tool handlers free of repetitive checks.</summary>
public sealed class Guard
{
    private readonly List<string> _errors = new();

    public Guard Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) _errors.Add($"{name} is required.");
        return this;
    }

    public Guard MaxLength(string? value, int max, string name)
    {
        if (value is not null && value.Length > max) _errors.Add($"{name} exceeds maximum length {max}.");
        return this;
    }

    public Guard SafeId(string? value, string name)
    {
        if (value is not null && !Ids.IsSafeId(value)) _errors.Add($"{name} contains invalid characters.");
        return this;
    }

    public Guard Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) _errors.Add($"{name} must be between {min} and {max}.");
        return this;
    }

    public Guard Range(long value, long min, long max, string name)
    {
        if (value < min || value > max) _errors.Add($"{name} must be between {min} and {max}.");
        return this;
    }

    public Guard MaxItems<T>(IReadOnlyCollection<T>? items, int max, string name)
    {
        if (items is not null && items.Count > max) _errors.Add($"{name} exceeds maximum of {max} items.");
        return this;
    }

    public Guard Must(bool condition, string message)
    {
        if (!condition) _errors.Add(message);
        return this;
    }

    public Guard Enum<TEnum>(string? value, string name, out TEnum? parsed) where TEnum : struct
    {
        parsed = null;
        if (value is null) return this;
        if (System.Enum.TryParse<TEnum>(value, ignoreCase: true, out var p)) parsed = p;
        else _errors.Add($"{name} must be one of: {string.Join(", ", System.Enum.GetNames(typeof(TEnum)))}.");
        return this;
    }

    public void Throw()
    {
        if (_errors.Count > 0) throw new ValidationException(_errors);
    }
}

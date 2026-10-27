using Social.BuildingBlocks.Domain;

namespace Accounts.Domain;

/// <summary>
/// The name people find an account by: 3 to 30 letters, digits and underscores. It keeps the case
/// its owner typed, and two handles that differ only in case are the same handle.
/// </summary>
public sealed class Handle : IEquatable<Handle>
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    private Handle(string value) => Value = value;

    /// <summary>As its owner typed it.</summary>
    public string Value { get; }

    /// <summary>The form two handles are compared in: lower case.</summary>
    public string Normalized => Value.ToLowerInvariant();

    public static Result<Handle> Create(string? text)
    {
        if (text is null || text.Length is < MinLength or > MaxLength)
        {
            return Result.Failure<Handle>(AccountErrors.HandleLength);
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return Result.Failure<Handle>(AccountErrors.HandleCharacters);
            }
        }

        return Result.Success(new Handle(text));
    }

    /// <summary>A handle read back from storage, where it was checked when it was written.</summary>
    public static Handle FromStored(string value) => new(value);

    public bool Equals(Handle? other) => other is not null && Normalized == other.Normalized;

    public override bool Equals(object? obj) => obj is Handle other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Normalized);

    public override string ToString() => Value;
}

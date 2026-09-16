using System.ComponentModel.DataAnnotations;

namespace Dragonmind.Core.Application.Utilities;

/// <summary>
/// Validates that a string property is not empty or whitespace-only.
/// <see cref="RequiredAttribute"/> rejects null and empty strings but allows
/// whitespace-only values; this attribute fills that gap at the validation
/// pipeline boundary.
/// <para>
/// Null values are considered valid by this attribute; pair with
/// <see cref="RequiredAttribute"/> to reject null values separately.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NonWhitespaceStringAttribute : ValidationAttribute
{
    public NonWhitespaceStringAttribute()
        : base("The {0} field must not be empty or whitespace-only.")
    {
    }

    /// <inheritdoc />
    public override bool IsValid(object? value) =>
        value is null || (value is string s && !string.IsNullOrWhiteSpace(s));
}

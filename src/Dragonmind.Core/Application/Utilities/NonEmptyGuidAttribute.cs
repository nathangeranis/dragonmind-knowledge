using System.ComponentModel.DataAnnotations;

namespace Dragonmind.Core.Application.Utilities;

/// <summary>
/// Validates that a Guid property is not Guid.Empty.
/// <see cref="RequiredAttribute"/> does not reject <see cref="Guid.Empty"/> for
/// non-nullable value types, so this attribute provides the missing guard at the
/// validation pipeline boundary.
/// <para>
/// Null values are considered valid by this attribute; pair with
/// <see cref="RequiredAttribute"/> to reject null values separately.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
    public NonEmptyGuidAttribute()
        : base("The {0} field must not be an empty Guid.")
    {
    }

    /// <inheritdoc />
    public override bool IsValid(object? value) =>
        value is null || (value is Guid guid && guid != Guid.Empty);
}

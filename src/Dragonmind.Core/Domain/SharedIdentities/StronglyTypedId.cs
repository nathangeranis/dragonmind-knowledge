using System.Text.Json;
using System.Text.Json.Serialization;

using Dragonmind.Core.Domain;

namespace Dragonmind.Core.Domain.SharedIdentities;

/// <summary>
/// JSON converter factory for all <see cref="StronglyTypedId{T}"/> subtypes.
/// Serializes as the underlying <see cref="Guid"/> value so that
/// <c>System.Text.Json</c> can round-trip these types even though their
/// constructors are private.
/// </summary>
public sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc/>
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsClass || typeToConvert.IsAbstract)
            return false;

        var baseType = typeToConvert.BaseType;
        while (baseType is not null)
        {
            if (baseType.IsGenericType &&
                baseType.GetGenericTypeDefinition() == typeof(StronglyTypedId<>))
                return true;
            baseType = baseType.BaseType;
        }
        return false;
    }

    /// <inheritdoc/>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(StronglyTypedIdJsonConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// JSON converter for a concrete <see cref="StronglyTypedId{T}"/> subtype.
/// Reads/writes the underlying <see cref="Guid"/> value.
/// </summary>
/// <typeparam name="T">The concrete strongly-typed ID type.</typeparam>
public sealed class StronglyTypedIdJsonConverter<T> : JsonConverter<T>
    where T : StronglyTypedId<T>
{
    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var guid = reader.GetGuid();
        return StronglyTypedId<T>.CreateFrom(guid);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Base class for strongly-typed identifiers used across bounded contexts.
/// Provides type safety and prevents mixing different types of IDs.
/// </summary>
/// <typeparam name="T">The concrete type of the strongly-typed ID</typeparam>
[JsonConverter(typeof(StronglyTypedIdJsonConverterFactory))]
public abstract class StronglyTypedId<T> : ValueObject where T : StronglyTypedId<T>
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.ConstructorInfo> ConstructorCache = new();

    public Guid Value { get; }

    protected StronglyTypedId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ID cannot be empty", nameof(value));
        }

        Value = value;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();

    /// <summary>
    /// Creates a new instance with a new Guid value.
    /// </summary>
    public static T NewId()
    {
        return CreateFrom(Guid.NewGuid());
    }

    /// <summary>
    /// Creates an instance from an existing Guid value.
    /// </summary>
    public static T CreateFrom(Guid value)
    {
        // Use cached reflection to create an instance of the derived type
        var constructorInfo = ConstructorCache.GetOrAdd(typeof(T), type =>
        {
            var ctor = type.GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(Guid) },
                null);

            if (ctor == null)
            {
                throw new InvalidOperationException($"Type {type.Name} must have a constructor that accepts a Guid parameter");
            }

            return ctor;
        });

        return (T)constructorInfo.Invoke(new object[] { value });
    }

    /// <summary>
    /// Tries to parse a string representation of a Guid into a strongly-typed ID.
    /// </summary>
    public static bool TryParse(string? value, out T? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Guid.TryParse(value, out var guid))
        {
            return false;
        }

        try
        {
            result = CreateFrom(guid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static implicit operator Guid(StronglyTypedId<T> id)
    {
        ArgumentNullException.ThrowIfNull(id, nameof(id));
        return id.Value;
    }
}

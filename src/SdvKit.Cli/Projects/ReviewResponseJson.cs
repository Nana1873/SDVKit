using System.Text.Json;

namespace SdvKit.Cli;

internal sealed class ReviewResponseJson(string context)
{
    public void RequireExactObject(
        JsonElement value,
        HashSet<string> requiredProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The {context} response has an invalid JSON object shape.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name)
                || !observed.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The {context} response has an unknown or duplicate JSON member.");
            }
        }

        if (observed.Count != requiredProperties.Count)
        {
            throw new InvalidDataException(
                $"The {context} response is missing a required JSON member.");
        }
    }

    public void ValidateOptionalArray(
        JsonElement value,
        int maximumCount,
        Action<JsonElement> validateItem)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateRequiredArray(value, maximumCount, validateItem);
    }

    public void ValidateRequiredArray(
        JsonElement value,
        int maximumCount,
        Action<JsonElement> validateItem)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException(
                $"The {context} response has an invalid bounded array shape.");
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            validateItem(item);
        }
    }

    public int RequiredInt32(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                $"The {context} response has an invalid integer member.");
        }

        return result;
    }

    public bool RequiredBoolean(JsonElement value, string propertyName)
    {
        JsonElement property = value.GetProperty(propertyName);
        if (property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"The {context} response has an invalid Boolean member.");
        }

        return property.GetBoolean();
    }
}

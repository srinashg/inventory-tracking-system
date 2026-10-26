using TechStore.Services.Exceptions;

namespace TechStore.Services.Validation;

/// <summary>Collects field problems so a request reports all of them at once, then throws a single exception.</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = new List<string>();
            _errors[field] = messages;
        }

        messages.Add(message);
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
        {
            throw new ValidationFailedException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}

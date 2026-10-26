namespace TechStore.Services.Exceptions;

/// <summary>A referenced record does not exist. Becomes HTTP 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }
}

/// <summary>The request is valid but the current state forbids it (stock, status, duplicate). Becomes HTTP 409.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message)
    {
    }
}

/// <summary>The input is invalid on its own. Carries the problems per field. Becomes HTTP 400.</summary>
public class ValidationFailedException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more fields are invalid.")
    {
        Errors = errors;
    }
}

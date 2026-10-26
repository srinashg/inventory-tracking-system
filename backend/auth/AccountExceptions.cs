namespace TechStore.Auth;

/// <summary>The request needs a signed-in user and there is none (or the account no longer exists). Becomes HTTP 401.</summary>
public class NotSignedInException : Exception
{
    public NotSignedInException() : base("Sign in to continue.")
    {
    }
}

/// <summary>An account referred to by id does not exist.</summary>
public class AccountNotFoundException : Exception
{
    public AccountNotFoundException(long userId) : base($"User {userId} was not found.")
    {
    }
}

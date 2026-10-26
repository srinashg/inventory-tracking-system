namespace TechStore.Auth;

/// <summary>
/// Thrown for a wrong password, an unknown e-mail or a locked account. All three look the same on purpose,
/// so a response never reveals whether an account exists. Becomes HTTP 401.
/// </summary>
public class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException() : base("Invalid email or password.")
    {
    }
}

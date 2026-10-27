using Xunit;

namespace TechStore.Services.Tests;

// TEMPORARY: deliberately failing test to prove CI turns red. Never merged.
public class CiVerifyTests
{
    [Fact]
    public void This_test_is_supposed_to_fail() => Assert.Equal(3, 1 + 1);
}

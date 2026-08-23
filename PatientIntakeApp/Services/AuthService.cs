using PatientIntakeApp.Models;
using PatientIntakeApp.Services.Stores;

namespace PatientIntakeApp.Services;

public interface IAuthService
{
    Task<AppUser?> LoginAsync(string username, string password);
}

public class AuthService : IAuthService
{
    private readonly IUserStore _userStore;
    private readonly IPasswordHasher _passwordHasher;

    public AuthService(IUserStore userStore, IPasswordHasher passwordHasher)
    {
        _userStore = userStore;
        _passwordHasher = passwordHasher;
    }

    public async Task<AppUser?> LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return null;
        var user = await _userStore.FindActiveByUsernameAsync(username.Trim());
        if (user == null) return null;

        if (!_passwordHasher.Verify(password, user.PasswordHash, user.PasswordSalt)) return null;

        var role = UserRole.User;
        if (Enum.IsDefined(typeof(UserRole), user.Role))
        {
            role = (UserRole)user.Role;
        }

        return new AppUser
        {
            Id = user.Id,
            Username = user.Username,
            Role = role
        };
    }
}


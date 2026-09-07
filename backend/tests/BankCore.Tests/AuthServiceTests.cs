using BankingAPI.Models;
using BankingAPI.Services;
using BankingAPI.DTOs;
using FluentAssertions;
using Moq;

namespace BankCore.Tests;

/// <summary>
/// Tests for AuthService covering: registration, duplicate email rejection,
/// login success/failure, and JWT token generation.
/// </summary>
public class AuthServiceTests
{
    private static (AuthService svc, BankingAPI.Data.ApplicationDbContext ctx, Mock<IJwtService> jwtMock) Build()
    {
        var ctx = TestDbFactory.Create();
        var jwtMock = new Mock<IJwtService>();
        jwtMock.Setup(j => j.GenerateAccessToken(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
               .Returns("mock-access-token");
        jwtMock.Setup(j => j.GenerateRefreshToken(It.IsAny<Guid>()))
               .Returns("mock-refresh-token");
        var svc = new AuthService(ctx, jwtMock.Object);
        return (svc, ctx, jwtMock);
    }

    [Fact]
    public async Task Register_NewUser_ReturnsAuthResponse()
    {
        var (svc, _, _) = Build();
        var request = new RegisterRequest
        {
            Email = "newuser@bank.com",
            Password = "SecurePass123!",
            FirstName = "John",
            LastName = "Doe"
        };

        var response = await svc.RegisterAsync(request);

        response.Should().NotBeNull();
        response!.AccessToken.Should().Be("mock-access-token");
        response.RefreshToken.Should().Be("mock-refresh-token");
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsNull()
    {
        var (svc, ctx, _) = Build();
        await TestDbFactory.SeedUserAsync(ctx, "existing@bank.com");

        var request = new RegisterRequest
        {
            Email = "existing@bank.com",
            Password = "AnotherPass123!",
            FirstName = "Jane",
            LastName = "Smith"
        };

        var response = await svc.RegisterAsync(request);
        response.Should().BeNull("duplicate email should be rejected");
    }

    [Fact]
    public async Task Register_PasswordIsHashed_NotStoredInPlaintext()
    {
        var (svc, ctx, _) = Build();
        var plainPassword = "MySecretPassword!";
        await svc.RegisterAsync(new RegisterRequest
        {
            Email = "hash@bank.com",
            Password = plainPassword,
            FirstName = "Hash",
            LastName = "Test"
        });

        var user = ctx.Users.Single(u => u.Email == "hash@bank.com");
        user.Password.Should().NotBe(plainPassword, "password must be bcrypt hashed");
        BCrypt.Net.BCrypt.Verify(plainPassword, user.Password).Should().BeTrue();
    }

    [Fact]
    public async Task Register_NewUser_DefaultRoleIsCustomer()
    {
        var (svc, ctx, _) = Build();
        await svc.RegisterAsync(new RegisterRequest
        {
            Email = "role@bank.com",
            Password = "RolePass123!",
            FirstName = "Role",
            LastName = "Test"
        });

        var user = ctx.Users.Single(u => u.Email == "role@bank.com");
        user.Role.Should().Be(UserRole.Customer);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsAuthResponse()
    {
        var (svc, ctx, _) = Build();
        // Seed user via register so password is hashed consistently
        await svc.RegisterAsync(new RegisterRequest
        {
            Email = "login@bank.com",
            Password = "LoginPass123!",
            FirstName = "Login",
            LastName = "User"
        });

        var response = await svc.LoginAsync(new LoginRequest
        {
            Email = "login@bank.com",
            Password = "LoginPass123!"
        });

        response.Should().NotBeNull();
        response!.AccessToken.Should().Be("mock-access-token");
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsNull()
    {
        var (svc, ctx, _) = Build();
        await svc.RegisterAsync(new RegisterRequest
        {
            Email = "wrong@bank.com",
            Password = "CorrectPass123!",
            FirstName = "Wrong",
            LastName = "Pass"
        });

        var response = await svc.LoginAsync(new LoginRequest
        {
            Email = "wrong@bank.com",
            Password = "WrongPassword!"
        });

        response.Should().BeNull();
    }

    [Fact]
    public async Task Login_NonExistentUser_ReturnsNull()
    {
        var (svc, _, _) = Build();
        var response = await svc.LoginAsync(new LoginRequest
        {
            Email = "ghost@bank.com",
            Password = "AnyPass123!"
        });

        response.Should().BeNull();
    }

    [Fact]
    public async Task GetUserById_ExistingUser_ReturnsDto()
    {
        var (svc, ctx, _) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx, "getuser@bank.com");

        var dto = await svc.GetUserByIdAsync(user.Id);

        dto.Should().NotBeNull();
        dto!.Email.Should().Be("getuser@bank.com");
    }

    [Fact]
    public async Task GetUserById_NonExistentUser_ReturnsNull()
    {
        var (svc, _, _) = Build();
        var dto = await svc.GetUserByIdAsync(Guid.NewGuid());
        dto.Should().BeNull();
    }
}

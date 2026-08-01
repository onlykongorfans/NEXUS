namespace ZORGATH.WebPortal.API.Contracts;

public record RequestAccountPasswordRecoveryDTO(string EmailAddress);

public record RequestAccountPasswordResetDTO(string EmailAddress);

public record ConfirmAccountPasswordResetDTO(string Token, string Password, string ConfirmPassword);

public record RequestAccountPasswordUpdateDTO(string CurrentPassword, string Password, string ConfirmPassword);

public record ConfirmAccountPasswordUpdateDTO(string Token);

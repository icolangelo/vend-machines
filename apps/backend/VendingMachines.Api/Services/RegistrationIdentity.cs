namespace VendingMachines.Api.Services;

public static class RegistrationIdentity
{
    public static string NormalizeEmail(string? email) =>
        email?.Trim().ToLowerInvariant() ?? string.Empty;

    public static string NormalizeCpf(string? cpf) =>
        new((cpf ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    public static string NormalizeCnpj(string? cnpj) =>
        new((cnpj ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    public static bool IsValidCpf(string? value)
    {
        var cpf = NormalizeCpf(value);
        if (cpf.Length != 11 || cpf.All(digit => digit == cpf[0]))
            return false;

        var firstDigit = CalculateCpfDigit(cpf.AsSpan(0, 9), 10);
        if (cpf[9] - '0' != firstDigit)
            return false;

        var secondDigit = CalculateCpfDigit(cpf.AsSpan(0, 10), 11);
        return cpf[10] - '0' == secondDigit;
    }

    public static bool IsValidCnpj(string? value)
    {
        var cnpj = NormalizeCnpj(value);
        if (cnpj.Length != 14 || cnpj.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            cnpj.All(character => character == cnpj[0]))
            return false;

        var firstDigit = CalculateCnpjDigit(cnpj.AsSpan(0, 12));
        if (cnpj[12] - '0' != firstDigit)
            return false;

        var secondDigit = CalculateCnpjDigit(cnpj.AsSpan(0, 13));
        return cnpj[13] - '0' == secondDigit;
    }

    private static int CalculateCpfDigit(ReadOnlySpan<char> digits, int firstWeight)
    {
        var sum = 0;
        for (var index = 0; index < digits.Length; index++)
            sum += (digits[index] - '0') * (firstWeight - index);

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static int CalculateCnpjDigit(ReadOnlySpan<char> characters)
    {
        var sum = 0;
        for (var index = 0; index < characters.Length; index++)
        {
            var characterValue = characters[index] - 48;
            var distanceFromRight = characters.Length - 1 - index;
            var weight = 2 + (distanceFromRight % 8);
            sum += characterValue * weight;
        }

        var remainder = sum % 11;
        return remainder is 0 or 1 ? 0 : 11 - remainder;
    }
}

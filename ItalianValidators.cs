namespace MichMapper;

internal static class ItalianValidators
{
    public static bool IsValidVat(string value)
    {
        if (value.Length != 11 || !value.All(char.IsDigit))
            return false;

        int sum = 0;

        for (int i = 0; i < 10; i++)
        {
            int digit = value[i] - '0';

            if (i % 2 == 0)
            {
                sum += digit;
            }
            else
            {
                int doubled = digit * 2;
                sum += doubled > 9 ? doubled - 9 : doubled;
            }
        }

        int checkDigit = (10 - (sum % 10)) % 10;
        return checkDigit == value[10] - '0';
    }

    public static bool IsPlausibleFiscalCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim().ToUpperInvariant();

        return value.Length == 16 && value.All(char.IsLetterOrDigit)
            || value.Length == 11 && value.All(char.IsDigit);
    }
}

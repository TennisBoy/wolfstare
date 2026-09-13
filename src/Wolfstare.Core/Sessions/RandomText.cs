using System.Security.Cryptography;

namespace Wolfstare.Core.Sessions;

/// <summary>Generates the random string a <see cref="RandomTextLock"/> makes the user retype.</summary>
public static class RandomText
{
    // No 0/O, 1/l/I — retyping thousands of characters is hard enough without look-alikes that
    // make a mismatch impossible to spot.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }
}

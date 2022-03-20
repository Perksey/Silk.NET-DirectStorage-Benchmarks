using System.Security.Cryptography;

File.WriteAllBytes("file.bin", RandomNumberGenerator.GetBytes(1024 * 1024 * 1024));
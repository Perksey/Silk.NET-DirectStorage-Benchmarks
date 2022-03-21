using System.Security.Cryptography;

var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 * 1024);

const int step = 256 * 1024;
for (var i = 0; i < bytes.Length; i += step)
{
    File.WriteAllBytes($"../DirectStorageVulkanTest/Data/file{i / step}.bin", bytes[i..(i + step)]);
}

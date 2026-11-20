using System.Reflection;

[assembly: AssemblyTitle(OrbitEvolved.Product.Name)]
[assembly: AssemblyProduct(OrbitEvolved.Product.Name)]
[assembly: AssemblyDescription("Windows workflow viewer for Codex")]

namespace OrbitEvolved
{
    internal static class Product
    {
        public const string Name = "Orbit E(volved)";
        public const string DataDirectory = ".orbit-evolved";
        public const string LegacyDataDirectory = ".orbit";
    }
}

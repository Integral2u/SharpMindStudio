using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Recipes;

/// <summary>Reads and writes <c>.smmtx</c> files without interpreting them.</summary>
public static class RecipeStore
{
    public static Recipe Load(string path) => JsonFile.Read<Recipe>(path);

    public static Recipe Parse(string json) => JsonFile.Parse<Recipe>(json);

    public static void Save(string path, Recipe recipe) => JsonFile.Write(path, recipe);
}

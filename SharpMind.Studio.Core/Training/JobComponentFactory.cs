using System.Globalization;
using SharpMind.Data.Metadata;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Builds and inspects the data components in a training job. The editor uses
/// the same descriptor metadata the runner uses to construct sources and stages,
/// so a component added in the UI is guaranteed to be resolvable at run time.
/// </summary>
public static class JobComponentFactory
{
    /// <summary>Creates a component with every constructor parameter at its declared default.</summary>
    public static JobComponent Create(ComponentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var component = new JobComponent
        {
            DisplayName = descriptor.Name,
            TypeName = descriptor.Type.AssemblyQualifiedName
                       ?? descriptor.Type.FullName
                       ?? descriptor.Type.Name,
        };

        foreach (ComponentParameter parameter in descriptor.Parameters)
            component.Args[parameter.Name] = DefaultValue(parameter);

        return component;
    }

    /// <summary>
    /// The value a new component starts with: the metadata default, then the
    /// optional parameter's C# default, then the first enum member.
    /// </summary>
    public static string DefaultValue(ComponentParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if (parameter.DefaultValue is { } declared) return declared.Value;
        if (parameter.Parameter.HasDefaultValue && parameter.Parameter.DefaultValue is { } value)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (parameter.Type.IsEnum)
            return Enum.GetNames(parameter.Type).FirstOrDefault() ?? "";
        return "";
    }

    /// <summary>Resolves a stored component back to its descriptor, or null when its plugin is missing.</summary>
    public static ComponentDescriptor? Find(JobComponent? component, IEnumerable<ComponentDescriptor> registry)
        => component is null ? null : ComponentRegistry.Find(component.TypeName, registry);
}

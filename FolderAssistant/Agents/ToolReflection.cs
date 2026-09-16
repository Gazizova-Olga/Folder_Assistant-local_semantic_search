using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// Turns a tool holder's public methods into the functions a model can call (SPEC-100). The holder's
/// methods already carry the model-facing contract — a <see cref="DescriptionAttribute"/> on the method
/// and on each parameter — so nothing is described twice: the reflection reads what the holder says,
/// and a method without a description is not a tool.
/// </summary>
internal static class ToolReflection
{
	/// <summary>
	/// One function per public instance method of <paramref name="holder"/> that carries a description,
	/// in declaration order, named after the method. A holder with none throws: a holder that yields
	/// no tools is a wiring mistake, not an agent with nothing to do.
	/// </summary>
	public static IReadOnlyList<AIFunction> Reflect(Object holder)
	{
		ArgumentNullException.ThrowIfNull(holder);

		Type type = holder.GetType();
		List<AIFunction> tools = [.. type
			.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Where(static method => method.GetCustomAttribute<DescriptionAttribute>() is not null)
			.OrderBy(static method => method.MetadataToken)
			.Select(method => AIFunctionFactory.Create(method, holder, options: null))];

		if (tools.Count == 0)
		{
			throw new InvalidOperationException($"{type.Name} has no public method carrying a [Description]; there is nothing to hand a model.");
		}

		return tools;
	}
}

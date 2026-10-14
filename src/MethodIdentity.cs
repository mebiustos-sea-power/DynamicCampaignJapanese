using System;
using System.Linq;
using System.Reflection;

namespace DynamicCampaignJapanese;

public static class MethodIdentity
{
    public static string Of(MethodBase method)
    {
        if (method.DeclaringType == null)
            throw new ArgumentException("A declaring type is required.", nameof(method));
        return TypeName(method.DeclaringType) + "::" + method.Name + "(" +
            string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")";
    }

    private static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericType)
            return type.GetGenericTypeDefinition().FullName!.Replace('+', '/') + "<" +
                string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        return (type.FullName ?? type.Name).Replace('+', '/');
    }
}

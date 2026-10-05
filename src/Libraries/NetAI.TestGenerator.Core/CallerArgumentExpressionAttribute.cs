
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{

    /// <summary>Captures the caller expression for an argument.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class CallerArgumentExpressionAttribute : Attribute
    {

        /// <summary>Initializes a caller argument expression attribute instance.</summary>
        public CallerArgumentExpressionAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        /// <summary>Gets the parameter name.</summary>
        public string ParameterName { get; }
    }
}
#endif
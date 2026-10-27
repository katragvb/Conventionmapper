using System;

namespace ConventionMapper.Testing
{
    /// <summary>A failed <see cref="MapperAssert"/> check.</summary>
    public sealed class MapperAssertException : Exception
    {
        /// <summary>Creates the exception with the failure description.</summary>
        public MapperAssertException(string message) : base(message)
        {
        }

        /// <summary>Creates the exception with the failure description and the exception that caused it.</summary>
        public MapperAssertException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}

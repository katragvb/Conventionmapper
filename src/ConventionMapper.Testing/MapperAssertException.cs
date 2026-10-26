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
    }
}

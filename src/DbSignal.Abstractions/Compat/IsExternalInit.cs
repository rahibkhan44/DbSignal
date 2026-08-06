#if NETSTANDARD2_0

// Records and `init` accessors need this type to exist. .NET Standard 2.0 predates it,
// so the compiler accepts an internal declaration instead. Never made public — two
// assemblies each declaring it publicly would collide.

using System.ComponentModel;

namespace System.Runtime.CompilerServices;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}

#endif

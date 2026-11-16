using Microsoft.EntityFrameworkCore;

namespace Repro.Data;

/// <summary>
/// A second service type for the same context instance. With <see cref="ReproOptions.AliasRegistration"/> on, the
/// worker resolves the context under both types, so the DI scope tracks (and disposes) the one instance twice.
/// </summary>
public interface ITranslationsContext
{
    DbSet<Translation> Translations { get; }
}

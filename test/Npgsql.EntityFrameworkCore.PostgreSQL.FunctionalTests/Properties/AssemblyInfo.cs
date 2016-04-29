using System.Reflection;
using System.Resources;
using Xunit;

[assembly: AssemblyTitle("Npgsql.EntityFrameworkCore.PostgreSQL.FunctionalTests")]
[assembly: AssemblyDescription("Functional test suite for Npgsql PostgreSQL provider for Entity Framework Core")]

// There seem to be some issues running in parallel... See #23
[assembly: CollectionBehavior(DisableTestParallelization = true)]

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using NpgsqlTypes;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal
{
    /// <summary>
    /// Provides translations for PostgreSQL full-text search methods.
    /// </summary>
    public class NpgsqlFullTextSearchMethodTranslator : IMethodCallTranslator
    {
        static readonly MethodInfo TsQueryParse =
            typeof(NpgsqlTsQuery).GetMethod(nameof(NpgsqlTsQuery.Parse), BindingFlags.Public | BindingFlags.Static);

        static readonly MethodInfo TsVectorParse =
            typeof(NpgsqlTsVector).GetMethod(nameof(NpgsqlTsVector.Parse), BindingFlags.Public | BindingFlags.Static);

        [NotNull]
        readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;
        readonly RelationalTypeMapping _boolMapping;
        readonly RelationalTypeMapping _tsQueryMapping;
        readonly RelationalTypeMapping _tsVectorMapping;
        readonly RelationalTypeMapping _regconfigMapping;

        public NpgsqlFullTextSearchMethodTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory, IRelationalTypeMappingSource typeMappingSource)
        {
            _sqlExpressionFactory = sqlExpressionFactory;
            _boolMapping = typeMappingSource.FindMapping(typeof(bool));
            _tsQueryMapping = typeMappingSource.FindMapping("tsquery");
            _tsVectorMapping = typeMappingSource.FindMapping("tsvector");
            _regconfigMapping = typeMappingSource.FindMapping("regconfig");
        }

        /// <inheritdoc />
        [CanBeNull]
        public SqlExpression Translate(SqlExpression instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments)
        {
            if (method == TsQueryParse || method == TsVectorParse)
                return _sqlExpressionFactory.Convert(arguments[0], method.ReturnType);

            if (method.DeclaringType == typeof(NpgsqlFullTextSearchDbFunctionsExtensions))
            {
                return method.Name switch
                {
                    // Methods accepting a configuration (regconfig)
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.ToTsVector)         when arguments.Count == 3 => ConfigAccepting("to_tsvector"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.PlainToTsQuery)     when arguments.Count == 3 => ConfigAccepting("plainto_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.PhraseToTsQuery)    when arguments.Count == 3 => ConfigAccepting("phraseto_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.ToTsQuery)          when arguments.Count == 3 => ConfigAccepting("to_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.WebSearchToTsQuery) when arguments.Count == 3 => ConfigAccepting("websearch_to_tsquery"),

                    // Methods not accepting a configuration
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.ArrayToTsVector)    => NonConfigAccepting("array_to_tsvector"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.ToTsVector)         => NonConfigAccepting("to_tsvector"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.PlainToTsQuery)     => NonConfigAccepting("plainto_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.PhraseToTsQuery)    => NonConfigAccepting("phraseto_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.ToTsQuery)          => NonConfigAccepting("to_tsquery"),
                    nameof(NpgsqlFullTextSearchDbFunctionsExtensions.WebSearchToTsQuery) => NonConfigAccepting("websearch_to_tsquery"),

                    _ => null
                };
            }

            if (method.DeclaringType == typeof(NpgsqlFullTextSearchLinqExtensions))
            {
                if (method.Name == nameof(NpgsqlFullTextSearchLinqExtensions.Rank) ||
                    method.Name == nameof(NpgsqlFullTextSearchLinqExtensions.RankCoverDensity))
                {
                    var rankFunctionName = method.Name == nameof(NpgsqlFullTextSearchLinqExtensions.Rank) ? "ts_rank" : "ts_rank_cd";

                    return arguments.Count switch
                    {
                        2 => _sqlExpressionFactory.Function(
                            rankFunctionName,
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[2],
                            typeof(float),
                            _sqlExpressionFactory.FindMapping(typeof(float))),

                        3 => _sqlExpressionFactory.Function(
                            rankFunctionName,
                            new[]
                            {
                                arguments[1].Type == typeof(float[]) ? arguments[1] : arguments[0],
                                arguments[1].Type == typeof(float[]) ? arguments[0] : arguments[1],
                                arguments[2]
                            },
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[3],
                            typeof(float),
                            _sqlExpressionFactory.FindMapping(typeof(float))),

                        4 => _sqlExpressionFactory.Function(
                            rankFunctionName,
                            new[] { arguments[1], arguments[0], arguments[2], arguments[3] },
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[4],
                            method.ReturnType,
                            _sqlExpressionFactory.FindMapping(typeof(float))),

                        _ => throw new ArgumentException($"Invalid method overload for {rankFunctionName}")
                    };
                }

                if (method.Name == nameof(NpgsqlFullTextSearchLinqExtensions.SetWeight))
                {
                    var newArgs = new List<SqlExpression>(arguments);
                    if (newArgs[1].Type == typeof(NpgsqlTsVector.Lexeme.Weight))
                        newArgs[1] = newArgs[1] is SqlConstantExpression weightExpression
                            ? _sqlExpressionFactory.Constant(weightExpression.Value.ToString()[0])
                            : throw new ArgumentException("Enum 'weight' argument for 'SetWeight' must be a constant expression.");
                    return _sqlExpressionFactory.Function(
                        "setweight",
                        newArgs,
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[2],
                        method.ReturnType);
                }

                return method.Name switch
                {
                    // Operators

                    nameof(NpgsqlFullTextSearchLinqExtensions.And) => QueryReturningOnTwoQueries("&&"),
                    nameof(NpgsqlFullTextSearchLinqExtensions.Or)  => QueryReturningOnTwoQueries("||"),

                    nameof(NpgsqlFullTextSearchLinqExtensions.ToNegative)
                        => new SqlUnaryExpression(ExpressionType.Not, arguments[0], arguments[0].Type, arguments[0].TypeMapping),

                    nameof(NpgsqlFullTextSearchLinqExtensions.Contains) => BoolReturningOnTwoQueries("@>"),
                    nameof(NpgsqlFullTextSearchLinqExtensions.IsContainedIn) => BoolReturningOnTwoQueries("<@"),

                    nameof(NpgsqlFullTextSearchLinqExtensions.Concat)
                        => _sqlExpressionFactory.Add(arguments[0], arguments[1], _tsVectorMapping),

                    nameof(NpgsqlFullTextSearchLinqExtensions.Matches) => new SqlCustomBinaryExpression(
                        _sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[0]),
                        arguments[1].Type == typeof(string)
                            ? _sqlExpressionFactory.Function(
                                "plainto_tsquery",
                                new[] { arguments[1] },
                                nullable: true,
                                argumentsPropagateNullability: TrueArrays[1],
                                typeof(NpgsqlTsQuery),
                                _tsQueryMapping)
                            : _sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[1]),
                        "@@",
                        typeof(bool),
                        _boolMapping),

                    // Functions

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetNodeCount)
                        => _sqlExpressionFactory.Function(
                            "numnode",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[1],
                            typeof(int),
                            _sqlExpressionFactory.FindMapping(method.ReturnType)),

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetQueryTree)
                        => _sqlExpressionFactory.Function(
                            "querytree",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[1],
                            typeof(string),
                            _sqlExpressionFactory.FindMapping(method.ReturnType)),

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetResultHeadline) when arguments.Count == 2
                        => _sqlExpressionFactory.Function(
                            "ts_headline",
                            new[] { arguments[1], arguments[0] },
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[2],
                            method.ReturnType),

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetResultHeadline) when arguments.Count == 3 => _sqlExpressionFactory.Function(
                        "ts_headline",
                        new[] { arguments[1], arguments[0], arguments[2] },
                        nullable: true,
                        argumentsPropagateNullability: TrueArrays[3],
                        method.ReturnType),

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetResultHeadline) when arguments.Count == 4 => _sqlExpressionFactory.Function(
                            "ts_headline",
                            new[]
                            {
                                // For the regconfig parameter, if a constant string was provided, just pass it as a string - regconfig-accepting functions
                                // will implicitly cast to regconfig. For (string!) parameters, we add an explicit cast, since regconfig actually is an OID
                                // behind the scenes, and for parameter binary transfer no type coercion occurs.
                                arguments[1] is SqlConstantExpression constant
                                    ? _sqlExpressionFactory.ApplyDefaultTypeMapping(constant)
                                    : _sqlExpressionFactory.Convert(arguments[1], typeof(string), _regconfigMapping),
                                arguments[2],
                                arguments[0],
                                arguments[3]
                            },
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[4],
                            method.ReturnType),

                    nameof(NpgsqlFullTextSearchLinqExtensions.Rewrite)
                        => _sqlExpressionFactory.Function(
                            "ts_rewrite",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[3],
                            typeof(NpgsqlTsQuery),
                            _tsQueryMapping),

                    nameof(NpgsqlFullTextSearchLinqExtensions.ToPhrase)
                        => _sqlExpressionFactory.Function(
                            "tsquery_phrase",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[arguments.Count],
                            typeof(NpgsqlTsQuery),
                            _tsQueryMapping),

                    nameof(NpgsqlFullTextSearchLinqExtensions.Delete)
                        => _sqlExpressionFactory.Function(
                            "ts_delete",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[2],
                            method.ReturnType,
                            _tsVectorMapping),

                    // TODO: Here we need to cast the char[] array we got into a "char"[] internal array...
                    nameof(NpgsqlFullTextSearchLinqExtensions.Filter)
                        => throw new NotImplementedException(),
                        //=> _sqlExpressionFactory.Function("ts_filter", arguments, typeof(NpgsqlTsVector), _tsVectorMapping),

                    nameof(NpgsqlFullTextSearchLinqExtensions.GetLength)
                        => _sqlExpressionFactory.Function(
                            "length",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[1],
                            method.ReturnType,
                            _sqlExpressionFactory.FindMapping(typeof(int))),

                    nameof(NpgsqlFullTextSearchLinqExtensions.ToStripped)
                        => _sqlExpressionFactory.Function(
                            "strip",
                            arguments,
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[arguments.Count],
                            method.ReturnType,
                            _tsVectorMapping),

                    _ => (SqlExpression)null
                };
            }

            return null;

            SqlExpression ConfigAccepting(string functionName)
                => _sqlExpressionFactory.Function(functionName, new[]
                    {
                        // For the regconfig parameter, if a constant string was provided, just pass it as a string - regconfig-accepting functions
                        // will implicitly cast to regconfig. For (string!) parameters, we add an explicit cast, since regconfig actually is an OID
                        // behind the scenes, and for parameter binary transfer no type coercion occurs.
                        arguments[1] is SqlConstantExpression constant
                            ? _sqlExpressionFactory.ApplyDefaultTypeMapping(constant)
                            : _sqlExpressionFactory.Convert(arguments[1], typeof(string), _regconfigMapping),
                        arguments[2]
                    },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[arguments.Count],
                    method.ReturnType,
                    _sqlExpressionFactory.FindMapping(method.ReturnType));

            SqlExpression NonConfigAccepting(string functionName)
                => _sqlExpressionFactory.Function(
                    functionName,
                    new[] { arguments[1] },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[arguments.Count],
                    method.ReturnType,
                    _sqlExpressionFactory.FindMapping(method.ReturnType));

            SqlCustomBinaryExpression QueryReturningOnTwoQueries(string @operator)
            {
                var inferredMapping = ExpressionExtensions.InferTypeMapping(arguments[0], arguments[1]);
                return new SqlCustomBinaryExpression(
                    _sqlExpressionFactory.ApplyTypeMapping(arguments[0], inferredMapping),
                    _sqlExpressionFactory.ApplyTypeMapping(arguments[1], inferredMapping),
                    @operator,
                    method.ReturnType,
                    inferredMapping);
            }

            SqlCustomBinaryExpression BoolReturningOnTwoQueries(string @operator)
            {
                return new SqlCustomBinaryExpression(
                    _sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[0]),
                    _sqlExpressionFactory.ApplyDefaultTypeMapping(arguments[1]),
                    @operator,
                    typeof(bool),
                    _boolMapping);
            }
        }
    }
}

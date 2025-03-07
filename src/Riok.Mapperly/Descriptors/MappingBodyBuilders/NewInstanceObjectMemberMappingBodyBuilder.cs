using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Riok.Mapperly.Abstractions;
using Riok.Mapperly.Descriptors.MappingBodyBuilders.BuilderContext;
using Riok.Mapperly.Descriptors.Mappings;
using Riok.Mapperly.Descriptors.Mappings.MemberMappings;
using Riok.Mapperly.Diagnostics;
using Riok.Mapperly.Helpers;
using Riok.Mapperly.Symbols;

namespace Riok.Mapperly.Descriptors.MappingBodyBuilders;

/// <summary>
/// Body builder for new instance object member mappings (mappings for which the target object gets created via <code>new()</code>).
/// </summary>
public static class NewInstanceObjectMemberMappingBodyBuilder
{
    public static void BuildMappingBody(MappingBuilderContext ctx, NewInstanceObjectMemberMapping mapping)
    {
        var mappingCtx = new NewInstanceBuilderContext<NewInstanceObjectMemberMapping>(ctx, mapping);
        BuildConstructorMapping(mappingCtx);
        BuildInitOnlyMemberMappings(mappingCtx, true);
        mappingCtx.AddDiagnostics();
    }

    public static void BuildMappingBody(MappingBuilderContext ctx, NewInstanceObjectMemberMethodMapping mapping)
    {
        var mappingCtx = new NewInstanceContainerBuilderContext<NewInstanceObjectMemberMethodMapping>(ctx, mapping);
        BuildConstructorMapping(mappingCtx);
        BuildInitOnlyMemberMappings(mappingCtx);
        ObjectMemberMappingBodyBuilder.BuildMappingBody(mappingCtx);
    }

    private static void BuildInitOnlyMemberMappings(INewInstanceBuilderContext<IMapping> ctx, bool includeAllMembers = false)
    {
        var initOnlyTargetMembers = includeAllMembers
            ? ctx.TargetMembers.Values.ToArray()
            : ctx.TargetMembers.Values.Where(x => x.CanOnlySetViaInitializer()).ToArray();
        foreach (var targetMember in initOnlyTargetMembers)
        {
            ctx.TargetMembers.Remove(targetMember.Name);

            if (ctx.MemberConfigsByRootTargetName.Remove(targetMember.Name, out var memberConfigs))
            {
                BuildInitMemberMapping(ctx, targetMember, memberConfigs);
                continue;
            }

            if (
                !MemberPath.TryFind(
                    ctx.Mapping.SourceType,
                    MemberPathCandidateBuilder.BuildMemberPathCandidates(targetMember.Name),
                    ctx.IgnoredSourceMemberNames,
                    out var sourceMemberPath
                )
            )
            {
                ctx.BuilderContext.ReportDiagnostic(
                    targetMember.IsRequired ? DiagnosticDescriptors.RequiredMemberNotMapped : DiagnosticDescriptors.SourceMemberNotFound,
                    targetMember.Name,
                    ctx.Mapping.TargetType,
                    ctx.Mapping.SourceType
                );
                continue;
            }

            BuildInitMemberMapping(ctx, targetMember, sourceMemberPath);
        }
    }

    private static void BuildInitMemberMapping(
        INewInstanceBuilderContext<IMapping> ctx,
        IMappableMember targetMember,
        IReadOnlyCollection<MapPropertyAttribute> memberConfigs
    )
    {
        // add configured mapping
        // target paths are not supported (yet), only target properties
        if (memberConfigs.Count > 1)
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.MultipleConfigurationsForInitOnlyMember,
                targetMember.Type,
                targetMember.Name
            );
        }

        var memberConfig = memberConfigs.First();
        if (memberConfig.Target.Count > 1)
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.InitOnlyMemberDoesNotSupportPaths,
                targetMember.Type,
                string.Join(".", memberConfig.Target)
            );
            return;
        }

        if (!MemberPath.TryFind(ctx.Mapping.SourceType, memberConfig.Source, out var sourceMemberPath))
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.SourceMemberNotFound,
                targetMember.Name,
                ctx.Mapping.TargetType,
                ctx.Mapping.SourceType
            );
            return;
        }

        BuildInitMemberMapping(ctx, targetMember, sourceMemberPath);
    }

    private static void BuildInitMemberMapping(
        INewInstanceBuilderContext<IMapping> ctx,
        IMappableMember targetMember,
        MemberPath sourcePath
    )
    {
        var targetPath = new MemberPath(new[] { targetMember });
        if (!ObjectMemberMappingBodyBuilder.ValidateMappingSpecification(ctx, sourcePath, targetPath, true))
            return;

        var delegateMapping =
            ctx.BuilderContext.FindMapping(sourcePath.MemberType, targetMember.Type)
            ?? ctx.BuilderContext.FindOrBuildMapping(sourcePath.MemberType.NonNullable(), targetMember.Type.NonNullable());

        if (delegateMapping == null)
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.CouldNotMapMember,
                ctx.Mapping.SourceType,
                sourcePath.FullName,
                sourcePath.Member.Type,
                ctx.Mapping.TargetType,
                targetPath.FullName,
                targetPath.Member.Type
            );
            return;
        }

        if (delegateMapping.Equals(ctx.Mapping))
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.ReferenceLoopInInitOnlyMapping,
                ctx.Mapping.SourceType,
                sourcePath.FullName,
                ctx.Mapping.TargetType,
                targetPath.FullName
            );
            return;
        }

        var nullFallback = NullFallbackValue.Default;
        if (!delegateMapping.SourceType.IsNullable() && sourcePath.IsAnyNullable())
        {
            nullFallback = ctx.BuilderContext.GetNullFallbackValue(targetMember.Type);
        }

        var memberMapping = new NullMemberMapping(
            delegateMapping,
            sourcePath,
            targetMember.Type,
            nullFallback,
            !ctx.BuilderContext.IsExpression
        );
        var memberAssignmentMapping = new MemberAssignmentMapping(targetPath, memberMapping);
        ctx.AddInitMemberMapping(memberAssignmentMapping);
    }

    private static void BuildConstructorMapping(INewInstanceBuilderContext<IMapping> ctx)
    {
        if (ctx.Mapping.TargetType is not INamedTypeSymbol namedTargetType)
        {
            ctx.BuilderContext.ReportDiagnostic(DiagnosticDescriptors.NoConstructorFound, ctx.BuilderContext.Target);
            return;
        }

        // attributed ctor is prio 1
        // parameterless ctor is prio 2
        // then by descending parameter count
        // ctors annotated with [Obsolete] are considered last unless they have a MapperConstructor attribute set
        var ctorCandidates = namedTargetType.InstanceConstructors
            .Where(ctor => ctor.IsAccessible())
            .OrderByDescending(x => x.HasAttribute(ctx.BuilderContext.Types.MapperConstructorAttribute))
            .ThenBy(x => x.HasAttribute(ctx.BuilderContext.Types.ObsoleteAttribute))
            .ThenByDescending(x => x.Parameters.Length == 0)
            .ThenByDescending(x => x.Parameters.Length);
        foreach (var ctorCandidate in ctorCandidates)
        {
            if (!TryBuildConstructorMapping(ctx, ctorCandidate, out var mappedTargetMemberNames, out var constructorParameterMappings))
            {
                if (ctorCandidate.HasAttribute(ctx.BuilderContext.Types.MapperConstructorAttribute))
                {
                    ctx.BuilderContext.ReportDiagnostic(
                        DiagnosticDescriptors.CannotMapToConfiguredConstructor,
                        ctx.Mapping.SourceType,
                        ctorCandidate
                    );
                }

                continue;
            }

            ctx.TargetMembers.RemoveRange(mappedTargetMemberNames);
            foreach (var constructorParameterMapping in constructorParameterMappings)
            {
                ctx.AddConstructorParameterMapping(constructorParameterMapping);
            }

            return;
        }

        ctx.BuilderContext.ReportDiagnostic(DiagnosticDescriptors.NoConstructorFound, ctx.BuilderContext.Target);
    }

    private static bool TryBuildConstructorMapping(
        INewInstanceBuilderContext<IMapping> ctx,
        IMethodSymbol ctor,
        [NotNullWhen(true)] out ISet<string>? mappedTargetMemberNames,
        [NotNullWhen(true)] out ISet<ConstructorParameterMapping>? constructorParameterMappings
    )
    {
        constructorParameterMappings = new HashSet<ConstructorParameterMapping>();
        mappedTargetMemberNames = new HashSet<string>();
        var skippedOptionalParam = false;
        foreach (var parameter in ctor.Parameters)
        {
            if (!TryFindConstructorParameterSourcePath(ctx, parameter, out var sourcePath))
            {
                // expressions do not allow skipping of optional parameters
                if (!parameter.IsOptional || ctx.BuilderContext.IsExpression)
                    return false;

                skippedOptionalParam = true;
                continue;
            }

            // nullability is handled inside the member mapping
            var paramType = parameter.Type.WithNullableAnnotation(parameter.NullableAnnotation);
            var delegateMapping =
                ctx.BuilderContext.FindMapping(sourcePath.MemberType, paramType)
                ?? ctx.BuilderContext.FindOrBuildMapping(sourcePath.Member.Type.NonNullable(), paramType.NonNullable());

            if (delegateMapping == null)
            {
                if (!parameter.IsOptional)
                    return false;

                skippedOptionalParam = true;
                continue;
            }

            if (delegateMapping.Equals(ctx.Mapping))
            {
                ctx.BuilderContext.ReportDiagnostic(
                    DiagnosticDescriptors.ReferenceLoopInCtorMapping,
                    ctx.Mapping.SourceType,
                    sourcePath.FullName,
                    ctx.Mapping.TargetType,
                    parameter.Name
                );
                return false;
            }

            var memberMapping = new NullMemberMapping(
                delegateMapping,
                sourcePath,
                paramType,
                ctx.BuilderContext.GetNullFallbackValue(paramType),
                !ctx.BuilderContext.IsExpression
            );
            var ctorMapping = new ConstructorParameterMapping(parameter, memberMapping, skippedOptionalParam);
            constructorParameterMappings.Add(ctorMapping);
            mappedTargetMemberNames.Add(parameter.Name);
        }

        return true;
    }

    private static bool TryFindConstructorParameterSourcePath(
        INewInstanceBuilderContext<IMapping> ctx,
        IParameterSymbol parameter,
        [NotNullWhen(true)] out MemberPath? sourcePath
    )
    {
        sourcePath = null;

        if (!ctx.MemberConfigsByRootTargetName.TryGetValue(parameter.Name, out var memberConfigs))
        {
            return MemberPath.TryFind(
                ctx.Mapping.SourceType,
                MemberPathCandidateBuilder.BuildMemberPathCandidates(parameter.Name),
                ctx.IgnoredSourceMemberNames,
                StringComparer.OrdinalIgnoreCase,
                out sourcePath
            );
        }

        if (memberConfigs.Count > 1)
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.MultipleConfigurationsForConstructorParameter,
                parameter.Type,
                parameter.Name
            );
        }

        var memberConfig = memberConfigs.First();
        if (memberConfig.Target.Count > 1)
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.ConstructorParameterDoesNotSupportPaths,
                parameter.Type,
                string.Join(".", memberConfig.Target)
            );
            return false;
        }

        if (!MemberPath.TryFind(ctx.Mapping.SourceType, memberConfig.Source, out sourcePath))
        {
            ctx.BuilderContext.ReportDiagnostic(
                DiagnosticDescriptors.SourceMemberNotFound,
                memberConfig.Source,
                ctx.Mapping.TargetType,
                ctx.Mapping.SourceType
            );
            return false;
        }

        return true;
    }
}

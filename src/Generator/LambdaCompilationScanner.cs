using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Lambdajection.Attributes;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lambdajection.Generator
{
    public class LambdaCompilationScanner
    {
        private const string ServiceCollectionDisplayName = "Microsoft.Extensions.DependencyInjection.IServiceCollection";
        private const string UseAwsServiceName = "UseAwsService";
        private readonly ImmutableArray<SyntaxTree> syntaxTrees;
        private readonly CSharpCompilation compilation;
        private readonly string lambdaTypeName;
        private readonly string startupDisplayName;
        private readonly Dictionary<string, ClassDeclarationSyntax> optionClasses = new Dictionary<string, ClassDeclarationSyntax>();
        private readonly HashSet<AwsServiceMetadata> awsServices = new HashSet<AwsServiceMetadata>();

        public LambdaCompilationScanner(CSharpCompilation compilation, ImmutableArray<SyntaxTree> syntaxTrees, string lambdaTypeName, string startupDisplayName)
        {
            this.compilation = compilation;
            this.syntaxTrees = syntaxTrees;
            this.lambdaTypeName = lambdaTypeName;
            this.startupDisplayName = startupDisplayName;
        }

        public LambdaCompilationScanResult Scan()
        {
            foreach (var tree in syntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                ScanTree(tree, semanticModel);
            }

            return new LambdaCompilationScanResult(optionClasses, awsServices);
        }

        public void ScanTree(SyntaxTree tree, SemanticModel semanticModel)
        {
            var classes = tree.GetRoot().DescendantNodesAndSelf().OfType<ClassDeclarationSyntax>();

            foreach (var classNode in classes)
            {
                var attributes = semanticModel.GetDeclaredSymbol(classNode)?.GetAttributes() ?? ImmutableArray.Create<AttributeData>();
                ScanForOptionClass(classNode, attributes);
                ScanForAwsServices(classNode, semanticModel);
            }
        }

        public void ScanForOptionClass(ClassDeclarationSyntax classNode, IEnumerable<AttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass?.Name != nameof(LambdaOptionsAttribute))
                {
                    continue;
                }

                var typeArg = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value!;
                var typeArgName = typeArg.ToDisplayString();
                var configSectionName = (string)attribute.ConstructorArguments[1].Value!;


                if (typeArgName == lambdaTypeName)
                {
                    optionClasses.Add(configSectionName, classNode);
                    return;
                }
            }
        }

        public void ScanForAwsServices(ClassDeclarationSyntax classNode, SemanticModel semanticModel)
        {
            if (semanticModel.GetDeclaredSymbol(classNode).ToDisplayString() != startupDisplayName)
            {
                return;
            }

            var serviceMetadatas = from invocation in classNode.DescendantNodes().OfType<InvocationExpressionSyntax>()
                                   where invocation.Expression is MemberAccessExpressionSyntax memberAccess
                                        && semanticModel.GetTypeInfo(memberAccess.Expression).Type?.ToDisplayString() == ServiceCollectionDisplayName
                                        && memberAccess.Name.Identifier.ValueText == UseAwsServiceName

                                   let memberAccess = (MemberAccessExpressionSyntax)invocation.Expression
                                   let genericName = (GenericNameSyntax)memberAccess.Name
                                   let interfaceType = genericName.TypeArgumentList.Arguments[0]
                                   let serviceType = semanticModel.GetTypeInfo(interfaceType).Type

                                   let interfaceName = serviceType.Name.ToString()
                                   let interfaceNameWithoutPrefix = interfaceName[1..]
                                   let serviceName = interfaceNameWithoutPrefix[6..]
                                   let implementationName = $"{interfaceNameWithoutPrefix}Client"
                                   let namespaceName = serviceType.ContainingNamespace.ToDisplayString()

                                   select new AwsServiceMetadata(serviceName, interfaceName, implementationName, namespaceName);

            awsServices.UnionWith(serviceMetadatas);
        }
    }
}
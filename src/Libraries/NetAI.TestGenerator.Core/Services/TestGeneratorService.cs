using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetAI.TestGenerator.Core
{
    public class TestGeneratorService
    {
        // Ermöglicht optionales Logging (z.B. für MSBuild oder Konsolenausgaben)
        public Action<string> LogMessage { get; set; } = (msg) => { };

        public void ProcessSourceFile(string sourceFilePath, string testProjectDirectory)
        {
            if (!File.Exists(sourceFilePath)) return;

            // 1. Quellklasse mit Roslyn parsen
            string sourceCode = File.ReadAllText(sourceFilePath);
            var sourceRoot = CSharpSyntaxTree.ParseText(sourceCode).GetCompilationUnitRoot();
            var targetClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            if (targetClass == null) return;

            string className = targetClass.Identifier.Text;
            string testClassName = $"{className}Tests";
            string testFilePath = Path.Combine(testProjectDirectory, $"{testClassName}.cs");

            // 2. Vorhandene Testmethoden scannen (Single Source of Truth)
            var existingTestMethods = GetExistingTestMethods(testFilePath);

            // 3. Alle Quellmethoden durchgehen
            var sourceMethods = targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var method in sourceMethods)
            {
                string methodName = method.Identifier.Text;

                // Einfaches Matcher-Konzept: Ist der Methodenname Teil eines vorhandenen Tests?
                bool testExists = existingTestMethods.Any(t => t.Contains(methodName, StringComparison.OrdinalIgnoreCase));

                if (testExists) continue;

                LogMessage($"[NetAI] Fehlender Test für '{methodName}' erkannt. Generiere per KI...");

                // 4. KI-Aufruf triggern
                string aiGeneratedMethod = CallAiToGenerateTest(methodName, method.ToString());

                // 5. In Testklasse schreiben (Variante 2: Alles in eine Klasse)
                if (!File.Exists(testFilePath))
                {
                    var namespaceDecl = targetClass.Parent as NamespaceDeclarationSyntax;
                    CreateNewTestClass(testFilePath, testClassName, namespaceDecl, aiGeneratedMethod);
                    existingTestMethods.Add(methodName); // Cache für die aktuelle Schleife erweitern
                }
                else
                {
                    AppendMethodToExistingClass(testFilePath, aiGeneratedMethod);
                }
            }
        }

        private List<string> GetExistingTestMethods(string testFilePath)
        {
            var methodNames = new List<string>();
            if (!File.Exists(testFilePath)) return methodNames;

            try
            {
                string testCode = File.ReadAllText(testFilePath);
                var root = CSharpSyntaxTree.ParseText(testCode).GetCompilationUnitRoot();
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

                foreach (var method in methods)
                {
                    methodNames.Add(method.Identifier.Text);
                }
            }
            catch
            {
                // Ignorieren bei korrupten Dateien, im Zweifel überschreiben
            }
            return methodNames;
        }

        private void CreateNewTestClass(string filePath, string testClassName, NamespaceDeclarationSyntax originalNamespace, string methodCode)
        {
            string namespaceName = originalNamespace?.Name.ToString() ?? "NetAI.Generated.Tests";
            if (!namespaceName.EndsWith(".Tests")) namespaceName += ".Tests";

            string rawTemplate =
$@"using Xunit;

namespace {namespaceName}
{{
    public class {testClassName}
    {{
    }}
}}";

            SyntaxTree tree = CSharpSyntaxTree.ParseText(rawTemplate);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();

            var newMethodNode = CSharpSyntaxTree.ParseText(methodCode).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
            var updatedClass = classDecl.AddMembers(newMethodNode);
            var newRoot = root.ReplaceNode(classDecl, updatedClass);

            // Datei via Roslyn sauber formatiert speichern
            var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(newRoot, new AdhocWorkspace());

            // Verzeichnis erstellen falls nötig
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, formattedRoot.ToFullString());
        }

        private void AppendMethodToExistingClass(string filePath, string methodCode)
        {
            string existingCode = File.ReadAllText(filePath);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(existingCode);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl == null) return;

            var newMethodNode = CSharpSyntaxTree.ParseText(methodCode).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();

            var updatedClass = classDecl.AddMembers(newMethodNode);
            var newRoot = root.ReplaceNode(classDecl, updatedClass);

            var formattedRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(newRoot, new AdhocWorkspace());
            File.WriteAllText(filePath, formattedRoot.ToFullString());
        }

        private string CallAiToGenerateTest(string methodName, string methodCode)
        {
            // TODO: Hier folgt Ihre KI-Integration (z.B. OpenAI API via HttpClient)
            // Wichtig für den Prompt: Die KI darf KEIN Markdown (```csharp) und keine umschließende 
            // Klasse zurückgeben. Nur die pure Methode mit dem Attribut.
            return $@"
        [Fact]
        public void {methodName}_Should_ExecuteSuccessfully()
        {{
            // Automatisch generierter Test für {methodName}
            Assert.True(true);
        }}";
        }
    }
}

namespace NetAI.TestGenerator.Core;

/// <summary>Provides the shared prompt templates used for test generation and compiler-error repair.</summary>
public static class PromptTemplates
{
    /// <summary>Template for generating a unit test from a method and its containing class.</summary>
    /// <summary>Template for generating a unit test from a method and its containing class.</summary>
    public const string UnitTestGenerator = """
                                            You are an expert in software quality and C# .NET 10 unit testing.
                                            Write a unit test for a method that compiles immediately.

                                            [FRAMEWORK REQUIREMENTS]
                                            - Test framework: {{ test_framework }}
                                            - Test-framework namespace: {{ test_framework_namespace }}
                                            - Test attribute to use: {{ test_attribute }}
                                            - Mocking library: {{ mocking_library }}
                                            - Mock-framework namespace (empty if no mocking is configured): {{ mock_framework_namespace }}

                                            [EXACT TARGET]
                                            Focus exclusively on the following method:
                                            - Method name: {{ ziel_methode_name }}
                                            - Signature/search term: `{{ ziel_methode_signatur }}`

                                            [CONTEXT: THE ENTIRE CLASS]
                                            Find the method defined above in this class:
                                            {{ klassen_code }}

                                            [STRICT ARCHITECTURE AND CODING RULES (ERROR-PREVENTION GUARDRAILS)]
                                            1. MOCK DATA & JSON: Never write JSON strings as raw text. Always create anonymous
                                               C# objects instead and serialize them to a string at runtime with System.Text.Json.
                                               Example:
                                               var data = new { choices = new[] { new { message = new { content = "xyz" } } } };
                                               string json = System.Text.Json.JsonSerializer.Serialize(data);

                                            2. OPTIONAL PARAMETERS: Does the target method have optional parameters?
                                               Always use named arguments when calling it in the 'Act' step.
                                               Example: `ct: CancellationToken.None`.
                                               This prevents type conflicts with preceding optional parameters.

                                            3. GENERICS / INTERFACES: Use the configured mocking library to properly mock
                                               all injected interfaces or repositories.

                                            4. NO EMPTY CATCH BLOCKS: In edge-case tests, simulate real exceptions
                                               if the method throws or catches them.

                                            [OUTPUT FORMAT]
                                            Return ONLY the raw C# code for the test file.
                                            Do not include explanations before or after the code block.
                                            Start directly with the required using directives, including "using {{ test_framework_namespace }};".
                                            Do not include any using directive for a package that is not present in the project.
                                            Never use "Newtonsoft.Json"; use "System.Text.Json" if JSON handling is needed.

                                            Structure your test methods perfectly using the AAA pattern:
                                            """;

    /// <summary>Template for repairing a generated test while retaining the original generation rules.</summary>
    /// <summary>Template for repairing a generated test while retaining the original generation rules.</summary>
    public const string UnitTestFixer = """
                                        The C# unit test you generated produced an error during compilation (dotnet build).

                                        Analyze the error message and the code step by step to fix the error.

                                        [FRAMEWORK CONTEXT]
                                        - Test framework: {{ test_framework }}
                                        - Test-framework namespace: {{ test_framework_namespace }}
                                        - Test attribute to use: {{ test_attribute }}
                                        - Mocking library: {{ mock_framework }}

                                        [COMPILER ERROR MESSAGES]
                                        {{ compiler_fehler }}

                                        [GENERATED CODE WITH ERRORS]
                                        {{ generierter_code }}

                                        [REPAIR INSTRUCTIONS]
                                        1. Identify the line and cause of the compiler error using the message above.
                                        2. Fix the code. Use the test framework stated above; do not introduce any other test or mocking framework.
                                        3. If a using directive is missing, add "using {{ test_framework_namespace }};" for test types, or "using {{ mock_framework_namespace }};" for mock types (only if a mock framework is configured).
                                        4. Ensure that no new syntax or type conflicts are introduced.

                                        [OUTPUT FORMAT]
                                        First write a single, short line stating the cause (e.g. "// Fix: resolved error CSXXXX on line XX").
                                        Then return ONLY the raw, corrected C# code. Do not include any other explanations. Start directly with the namespaces.
                                        """;

    /// <summary>Compact repair template that requests only the complete corrected code block.</summary>
    public const string UnitTestFixerSimple = """
                                              The C# unit test you generated produced an error during compilation (dotnet build).

                                              [FRAMEWORK CONTEXT]
                                              - Test framework: {{ test_framework }}
                                              - Test-framework namespace: {{ test_framework_namespace }}
                                              - Test attribute to use: {{ test_attribute }}
                                              - Mocking library: {{ mock_framework }}

                                              [CURRENT CODE WITH ERRORS]
                                              {{ aktuellerCode }}

                                              [COMPILER ERROR MESSAGES]
                                              {{ compiler_fehler }}

                                              [REPAIR INSTRUCTIONS]
                                              1. Analyze the provided code and error messages step by step internally to understand the causes.
                                              2. Fix the code. Use the test framework stated above; do not introduce any other test or mocking framework.
                                              3. Add all required using directives directly to the code (e.g. "using {{ test_framework_namespace }};" for test types). Never reference a package that is not part of the project; in particular, do NOT use "Newtonsoft.Json" - use "System.Text.Json" instead if JSON is needed.
                                              4. Ensure that no new syntax or type conflicts are introduced.

                                              [STRICT OUTPUT FORMAT]
                                              Return only the COMPLETE, corrected C# code. Do not include introductions, explanations, greetings, or text after the code.
                                              """;
}
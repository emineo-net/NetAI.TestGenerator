

namespace NetAI.TestGenerator.Core;

public static class PromptTemplates
{
    public const string UnitTestGenerator = """"
      You are an expert in software quality and C# .NET 10 unit testing.
      Write a unit test for a method that compiles immediately.

      [FRAMEWORK REQUIREMENTS]
      - Test framework: {{ test_framework }}
      - Mocking library: {{ mocking_library }}
    - Assertions: {{ assertion_library }}

      [EXACT TARGET]
      Focus exclusively on the following method:
      - Method name: {{ ziel_methode_name }}
      - Signature/search term: `{{ ziel_methode_signatur }}`

      [CONTEXT: THE ENTIRE CLASS]
      Find the method defined above in this class:
    ```csharp
    {{ klassen_code }}
    ```

    [STRICT ARCHITECTURE AND CODING RULES (ERROR-PREVENTION GUARDRAILS)]
    1. HTTPCLIENT MOCKING: If the class uses 'HttpClient',
       create a minimal 'FakeHttpMessageHandler : HttpMessageHandler'.
       Pass it to the HttpClient constructor.
       NEVER mock 'HttpClient' directly with NSubstitute/Moq.
       PostAsync/GetAsync are not virtual (this causes errors!).

    2. MOCK DATA & JSON: Never write JSON strings as raw text.
       Always create anonymous C# objects instead.
       Serialize them to a string at runtime with Newtonsoft.Json.
       Example:
       var data = new { choices = new[] { new { message = new { content = "xyz" } } } };
       string json = Newtonsoft.Json.JsonConvert.SerializeObject(data);

    3. OPTIONAL PARAMETERS: Does the target method have optional parameters?
       Always use named arguments when calling it in the 'Act' step.
       Example: `ct: CancellationToken.None`.
       This prevents type conflicts with preceding optional parameters.

    4. GENERICS / INTERFACES: Use the mocking library to properly mock
       all injected interfaces or repositories.

    5. NO EMPTY CATCH BLOCKS: In edge-case tests, simulate real exceptions
       if the method throws or catches them.

    [OUTPUT FORMAT]
    Return ONLY the raw C# code for the test file.
    Do not include explanations before or after the code block.
    Start directly with the required namespaces.

    Structure your test methods perfectly using the AAA pattern:
    """";




    public const string UnitTestFixer = """"
                                        The C# unit test you generated produced an error during compilation (dotnet build).

                                        Analyze the error message and the code step by step to fix the error.

                                        [COMPILER ERROR MESSAGES]
                                        {{ compiler_fehler }}

                                        [GENERATED CODE WITH ERRORS]
                                        ```csharp
                                        {{ generierter_code }}
                                        ```

                                        [REPAIR INSTRUCTIONS]
                                        1. Identify the line and cause of the compiler error using the message above.
                                        2. Fix the code while strictly following the original rules (HttpClient mocking via FakeHttpMessageHandler, correct C# string escaping).
                                        3. Ensure that no new syntax or type conflicts are introduced.

                                        [OUTPUT FORMAT]
                                        First write a single, short line stating the cause (e.g. "// Fix: resolved error CSXXXX on line XX").
                                        Then return ONLY the raw, corrected C# code. Do not include any other explanations before or after the code block. Start directly with the namespaces.
                                        """";

    public const string UnitTestFixerSimple = """"
                                              The C# unit test you generated produced an error during compilation (dotnet build).

                                              [CURRENT CODE WITH ERRORS]
                                              {{ aktuellerCode }}

                                              [COMPILER ERROR MESSAGES]
                                              {{ compiler_fehler }}

                                              [REPAIR INSTRUCTIONS]
                                              1. Analyze the provided code and error messages step by step internally to understand the causes.
                                              2. Mentally fix the code while strictly following the rules (HttpClient mocking via FakeHttpMessageHandler, correct C# string escaping).
                                              3. Add all required using directives (e.g. System.Text, Newtonsoft.Json) directly to the code.
                                              4. Ensure that no new syntax or type conflicts are introduced.

                                              [STRICT OUTPUT FORMAT]
                                              Return only the COMPLETE, corrected C# code block.
                                              Do not include introductions, explanations, greetings, or text after the code. Your response must start with ```csharp and end with ```.
                                              """";




}


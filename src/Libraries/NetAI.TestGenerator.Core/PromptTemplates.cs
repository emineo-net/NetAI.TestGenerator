using System;
using System.Collections.Generic;
using System.Text;

namespace NetAI.TestGenerator.Core;

public static class PromptTemplates
{
    public const string UnitTestGenerator = """
                                            Du bist ein Experte für Software-Qualität und C# .NET 10 Unit Tests.
                                            Deine Aufgabe ist es, für eine ganz bestimmte Methode einen professionellen Unit Test zu schreiben.

                                            [FRAMEWORK VORGABEN]
                                            - Test-Framework: {{ test_framework }}
                                            - Mocking-Bibliothek: {{ mocking_library }}

                                            [EXAKTES ZIEL]
                                            Fokussiere dich ausschließlich auf die folgende Methode. Achte genau auf die Parameter, um Verwechslungen bei Überladungen zu vermeiden:
                                            - Methoden-Name: {{ ziel_methode_name }}
                                            - Exakte Signatur: `{{ ziel_methode_signatur }}`

                                            [KONTEXT: DIE GANZE KLASSE]
                                            Hier ist der Quellcode der vollständigen Klasse. Suche darin nach der oben definierten Signatur:
                                            ```csharp
                                            {{ klassen_code }}
                                            ```

                                            [ANWEISUNGEN]
                                            1. Schreibe sauberen, kompilierbaren C#-Code nach dem AAA-Muster.
                                            2. Erstelle Tests, die genau zu den Parametern und dem Rückgabetyp der oben genannten Signatur passen.
                                            3. Gib NUR den reinen C#-Code des Test-Files zurück. Keine Erklärungen außerhalb des Codeblocks.

                                            Deine Test-Methoden:
                                            """;

}


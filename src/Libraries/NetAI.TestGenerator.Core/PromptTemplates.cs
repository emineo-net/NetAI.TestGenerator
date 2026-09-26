

namespace NetAI.TestGenerator.Core;

public static class PromptTemplates
{
    // Wir öffnen mit VIER Anführungszeichen. 
    // Dadurch darf der Text im Prompt drin drei Anführungszeichen (\"\"\") enthalten, ohne den String zu brechen!
    public const string UnitTestGenerator = """"
        Du bist ein Experte für Software-Qualität und C# .NET 10 Unit Tests.
        Deine Aufgabe ist es, für eine ganz bestimmte Methode einen professionellen, sofort kompilierbaren Unit Test zu schreiben.

        [FRAMEWORK VORGABEN]
        - Test-Framework: {{ test_framework }}
        - Mocking-Bibliothek: {{ mocking_library }}
        - Assertions: {{ assertion_library }}

        [EXAKTES ZIEL]
        Fokussiere dich ausschließlich auf die folgende Methode. Achte genau auf die Parameter, um Verwechslungen bei Überladungen zu vermeiden:
        - Methoden-Name: {{ ziel_methode_name }}
        - Ungefähre Signatur/Suchbegriff: `{{ ziel_methode_signatur }}`

        [KONTEXT: DIE GANZE KLASSE]
        Hier ist der Quellcode der vollständigen Klasse. Suche darin nach der oben definierten Methode:
        ```csharp
        {{ klassen_code }}
        ```

        [STRIKTE ARCHITEKTUR- UND CODERELGELN (ANTI-FEHLER-LEITPLANKEN)]
        1. HTTPCLIENT-MOCKING: Falls die Klasse 'HttpClient' verwendet, erstelle im Test-File einen minimalen 'FakeHttpMessageHandler : HttpMessageHandler' und übergib diesen an den HttpClient-Konstruktor. Mocke 'HttpClient' NIEMALS direkt mit NSubstitute/Moq (da PostAsync/GetAsync nicht virtuell sind und dies zu Kompilierfehlern führt!).
        2. STRING ESCAPING: Wenn du JSON-Strings oder komplexe Payloads für Mocks erstellst, nutze AUSSCHLIESSLICH C# 11 Raw String Literals mit doppelten Dollarzeichen, um Escaping-Fehler mit Anführungszeichen zu vermeiden. Beispiel: \$\$"""{ "key": "value" }"""
        3. OPTIONALE PARAMETER: Wenn die Zielmethode optionale Parameter besitzt, nutze beim Aufruf im 'Act'-Schritt zwingend benannte Argumente (e.g., `ct: CancellationToken.None`), um Typkonflikte mit vorherigen optionalen Parametern zu vermeiden.
        4. VERHALTEN BEI GENERIC / INTERFACES: Nutze die angegebene Mocking-Bibliothek, um alle übergebenen Interfaces oder Repositories sauber zu mocken.
        5. KEINE LEEREN CATCH-BLÖCKE: Simuliere in den Edge-Case-Tests echte Exceptions, falls die Methode diese wirft.

        [AUSGABEFORMAT]
        Gib NUR den reinen C#-Code des Test-Files zurück. Keine Erklärungen vor oder nach dem Codeblock. Beginne direkt mit den notwendigen Namespaces.

        Deine perfekt strukturierten Test-Methoden nach dem AAA-Muster:
        """";
}


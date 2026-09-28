

namespace NetAI.TestGenerator.Core;

public static class PromptTemplates
{
    // Wir öffnen mit VIER Anführungszeichen. 
    // Dadurch darf der Text im Prompt drin drei Anführungszeichen (\"\"\") enthalten, ohne den String zu brechen!
    public const string UnitTestGenerator = """"
    Du bist ein Experte für Software-Qualität und C# .NET 10 Unit Tests.
    Schreibe für eine Methode einen sofort kompilierbaren Unit Test.

    [FRAMEWORK VORGABEN]
    - Test-Framework: {{ test_framework }}
    - Mocking-Bibliothek: {{ mocking_library }}
    - Assertions: {{ assertion_library }}

    [EXAKTES ZIEL]
    Fokussiere dich ausschließlich auf die folgende Methode:
    - Methoden-Name: {{ ziel_methode_name }}
    - Signatur/Suchbegriff: `{{ ziel_methode_signatur }}`

    [KONTEXT: DIE GANZE KLASSE]
    Suche in dieser Klasse nach der oben definierten Methode:
    ```csharp
    {{ klassen_code }}
    ```

    [STRIKTE ARCHITEKTUR- UND CODERELGELN (ANTI-FEHLER-LEITPLANKEN)]
    1. HTTPCLIENT-MOCKING: Falls die Klasse 'HttpClient' verwendet,
       erstelle einen minimalen 'FakeHttpMessageHandler : HttpMessageHandler'.
       Übergib diesen an den HttpClient-Konstruktor.
       Mocke 'HttpClient' NIEMALS direkt mit NSubstitute/Moq.
       PostAsync/GetAsync sind nicht virtuell (führt zu Fehlern!).

    2. MOCK DATA & JSON: Schreibe JSON-Strings niemals als rohen Text.
       Erstelle stattdessen immer anonyme C#-Objekte.
       Serialisiere sie zur Laufzeit mit Newtonsoft.Json in einen String.
       Beispiel:
       var data = new { choices = new[] { new { message = new { content = "xyz" } } } };
       string json = Newtonsoft.Json.JsonConvert.SerializeObject(data);

    3. OPTIONALE PARAMETER: Besitzt die Zielmethode optionale Parameter?
       Nutze beim Aufruf im 'Act'-Schritt zwingend benannte Argumente.
       Beispiel: `ct: CancellationToken.None`.
       Das verhindert Typkonflikte mit vorherigen optionalen Parametern.

    4. VERHALTEN BEI GENERIC / INTERFACES: Nutze die Mocking-Bibliothek,
       um alle übergebenen Interfaces oder Repositories sauber zu mocken.

    5. KEINE LEEREN CATCH-BLÖCKE: Simuliere in den Edge-Case-Tests
       echte Exceptions, falls die Methode diese wirft oder fängt.

    [AUSGABEFORMAT]
    Gib NUR den reinen C#-Code des Test-Files zurück.
    Keine Erklärungen vor oder nach dem Codeblock.
    Beginne direkt mit den notwendigen Namespaces.

    Deine perfekt strukturierten Test-Methoden nach dem AAA-Muster:
    """";




    public const string UnitTestFixer = """"
                                        Der von dir generierte C#-Unit-Test hat beim Kompilieren (dotnet build) einen Fehler erzeugt.

                                        Analysiere die Fehlermeldung und den Code Schritt für Schritt, um den Fehler zu beheben.

                                        [COMPILER FEHLERMELDUNGEN]
                                        {{ compiler_fehler }}

                                        [GENERIERTER CODE MIT FEHLERN]
                                        ```csharp
                                        {{ generierter_code }}
                                        ```

                                        [ANWEISUNG ZUR FEHLERBEHEBUNG]
                                        1. Identifiziere die Zeile und die Ursache des Compiler-Fehlers anhand der obigen Meldung.
                                        2. Korrigiere den Code unter strikter Einhaltung der ursprünglichen Regeln (HttpClient-Mocking via FakeHttpMessageHandler, korrektes C# String-Escaping).
                                        3. Stelle sicher, dass keine neuen Syntax- oder Typkonflikte entstehen.

                                        [AUSGABEFORMAT]
                                        Schreibe zuerst eine einzige, kurze Zeile mit der Ursache (z. B. "// Fix: Fehler CSXXXX in Zeile XX behoben").
                                        Gib danach NUR den reinen, korrigierten C#-Code zurück. Keine weiteren Erklärungen vor oder nach dem Codeblock. Beginne direkt mit den Namespaces.
                                        """";

    //public const string UnitTestFixerSimple = """"
    //                                    Der von dir generierte C#-Unit-Test hat beim Kompilieren (dotnet build) einen Fehler erzeugt.

    //                                    Analysiere die Fehlermeldung und den Code Schritt für Schritt, um den Fehler zu beheben.

    //                                    [COMPILER FEHLERMELDUNGEN]
    //                                    {{ compiler_fehler }}

    //                                    [ANWEISUNG ZUR FEHLERBEHEBUNG]
    //                                    1. Identifiziere die Zeile und die Ursache des Compiler-Fehlers anhand der obigen Meldung.
    //                                    2. Korrigiere den Code unter strikter Einhaltung der ursprünglichen Regeln (HttpClient-Mocking via FakeHttpMessageHandler, korrektes C# String-Escaping).
    //                                    3. Stelle sicher, dass keine neuen Syntax- oder Typkonflikte entstehen.

    //                                    [AUSGABEFORMAT]
    //                                    Gib den reinen korrigierten C#-Code zurück. 
    //                                    Eine kurze Erklärung deiner Änderungen
    //                                    """";




    public const string UnitTestFixerSimple = """"
                                              Der von dir generierte C#-Unit-Test hat beim Kompilieren (dotnet build) einen Fehler erzeugt.

                                              [AKTUELLER FEHLERHAFTER CODE]
                                              {{ aktuellerCode }}

                                              [COMPILER FEHLERMELDUNGEN]
                                              {{ compiler_fehler }}

                                              [ANWEISUNG ZUR FEHLERBEHEBUNG]
                                              1. Analysiere den bereitgestellten Code und die Fehlermeldungen intern Schritt für Schritt, um die Ursachen zu verstehen.
                                              2. Korrigiere den Code im Geiste unter strikter Einhaltung der Regeln (HttpClient-Mocking via FakeHttpMessageHandler, korrektes C# String-Escaping).
                                              3. Füge alle notwendigen using-Direktiven (z.B. System.Text, Newtonsoft.Json) direkt in den Code ein.
                                              4. Stelle sicher, dass keine neuen Syntax- oder Typkonflikte entstehen.

                                              [STRIKTES AUSGABEFORMAT]
                                              Gib ausschließlich den VOLLSTÄNDIGEN, korrigierten C#-Codeblock zurück.
                                              Verzichte komplett auf Einleitungen, Erklärungen, Grüße oder Text nach dem Code. Deine Antwort darf nur mit ```csharp beginnen und mit ``` enden.
                                              """";




}


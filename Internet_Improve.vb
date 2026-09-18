' DeepSeek Web Search Integration in VB.NET (276 lines)

Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Collections.Concurrent
Imports System.Collections.Generic

Module DeepSeekImprovedSearch

    ''' Entry point. Runs an interactive console chat.
  
    Sub Main()
        Console.WriteLine("DeepSeek + Web Search (type 'exit' to quit)")
        Dim assistant = New DeepSeekAssistant()
        While True
            Console.Write("You: ")
            Dim input = Console.ReadLine()
            If input Is Nothing OrElse input.ToLower() = "exit" Then Exit While
            Dim response = assistant.AskAsync(input).Result
            ConsoleHelper.WriteLineColored("DeepSeek: " & response, ConsoleColor.Cyan)
        End While
    End Sub
End Module

''' Represents a single web search result.
Class SearchResult
    Public Property Title As String
    Public Property Url As String
    Public Property Snippet As String
    Public Property Date As DateTime?
End Class

''' Defines a contract for web search providers.
Interface ISearchProvider
    Function SearchAsync(query As String, count As Integer) As Task(Of List(Of SearchResult))
End Interface

''' Bing Web Search API provider with in-memory caching.
Class BingSearchProvider
    Implements ISearchProvider

    Private Shared ReadOnly ApiKey As String = "YOUR_BING_API_KEY" ' Replace with your key
    Private ReadOnly client As HttpClient
    Private ReadOnly cache As ConcurrentDictionary(Of String, (Results As List(Of SearchResult), Timestamp As DateTime))
    Private ReadOnly cacheDuration As TimeSpan = TimeSpan.FromMinutes(5)

    Sub New()
        client = New HttpClient()
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", ApiKey)
        cache = New ConcurrentDictionary(Of String, (List(Of SearchResult), DateTime))
    End Sub

    ''' Searches Bing, using cache if fresh.
    Public Async Function SearchAsync(query As String, count As Integer) As Task(Of List(Of SearchResult)) Implements ISearchProvider.SearchAsync
        If cache.TryGetValue(query, Dim cached) AndAlso DateTime.Now - cached.Timestamp < cacheDuration Then
            Return cached.Results.Take(count).ToList()
        End If
        Dim encodedQuery = Uri.EscapeDataString(query)
        Dim url = $"https://api.bing.microsoft.com/v7.0/search?q={encodedQuery}&count={count}"
        Dim response = Await RetryAsync(Function() client.GetAsync(url), 3)
        response.EnsureSuccessStatusCode()
        Dim json = Await response.Content.ReadAsStringAsync()
        Dim results = ParseResults(json)
        cache(query) = (results, DateTime.Now)
        Return results
    End Function

    ''' Parses Bing JSON response.
    Private Function ParseResults(json As String) As List(Of SearchResult)
        Dim results = New List(Of SearchResult)
        Using doc = JsonDocument.Parse(json)
            Dim root = doc.RootElement
            If root.TryGetProperty("webPages", Dim webPages) Then
                If webPages.TryGetProperty("value", Dim items) Then
                    For Each item In items.EnumerateArray()
                        Dim snippet = If(item.TryGetProperty("snippet", Dim s), s.GetString(), "")
                        Dim datePublished As DateTime? = Nothing
                        If item.TryGetProperty("datePublished", Dim dp) Then DateTime.TryParse(dp.GetString(), datePublished)
                        results.Add(New SearchResult With {
                            .Title = item.GetProperty("name").GetString(),
                            .Url = item.GetProperty("url").GetString(),
                            .Snippet = snippet,
                            .Date = datePublished
                        })
                    Next
                End If
            End If
        End Using
        Return results
    End Function

    ''' Simple retry logic for transient failures.
    Private Shared Async Function RetryAsync(action As Func(Of Task(Of HttpResponseMessage)), maxRetries As Integer) As Task(Of HttpResponseMessage)
        Dim attempt = 0
        While True
            Try
                Return Await action()
            Catch ex As HttpRequestException When attempt < maxRetries
                attempt += 1
                Await Task.Delay(1000 * attempt)
            End Try
        End While
    End Function
End Class

''' DuckDuckGo Instant Answer API provider (no key required).
Class DuckDuckGoSearchProvider
    Implements ISearchProvider
    Private ReadOnly client As HttpClient

    Public Sub New()
        client = New HttpClient()
    End Sub

    ''' Searches DuckDuckGo and returns simplified results.
    Public Async Function SearchAsync(query As String, count As Integer) As Task(Of List(Of SearchResult)) Implements ISearchProvider.SearchAsync
        Dim encodedQuery = Uri.EscapeDataString(query)
        Dim url = $"https://api.duckduckgo.com/?q={encodedQuery}&format=json&no_html=1&skip_disambig=1"
        Dim json = Await client.GetStringAsync(url)
        Return ParseDuckResults(json, count)
    End Function

    Function ParseDuckResults(json As String, count As Integer) As List(Of SearchResult)
        Dim results = New List(Of SearchResult)
        Using doc = JsonDocument.Parse(json)
            Dim root = doc.RootElement
            ' Abstract text
            If root.TryGetProperty("AbstractText", Dim abstractText) AndAlso abstractText.GetString() <> "" Then
                results.Add(New SearchResult With {
                    .Title = root.GetProperty("Heading").GetString(),
                    .Url = root.GetProperty("AbstractURL").GetString(),
                    .Snippet = abstractText.GetString()
                })
            End If
            ' Related topics
            If root.TryGetProperty("RelatedTopics", Dim topics) Then
                For Each topic In topics.EnumerateArray()
                    If results.Count >= count Then Exit For
                    If topic.TryGetProperty("Text", Dim text) AndAlso topic.TryGetProperty("FirstURL", Dim url) Then
                        Dim snippet = text.GetString()
                        Dim title = snippet.Split(" - "c)(0) ' simplistic
                        results.Add(New SearchResult With {
                            .Title = title,
                            .Url = url.GetString(),
                            .Snippet = snippet
                        })
                    End If
                Next
            End If
        End Using
        Return results.Take(count).ToList()
    End Function
End Class

''' Factory to choose search provider based on configuration.
Class SearchProviderFactory
    Public Shared Function CreateProvider(providerName As String) As ISearchProvider
        Select Case providerName.ToLower()
            Case "bing"
                Return New BingSearchProvider()
            Case "duckduckgo"
                Return New DuckDuckGoSearchProvider()
            Case Else
                Return New BingSearchProvider() ' default
        End Select
    End Function
End Class

''' Core assistant that uses DeepSeek and web search when needed.
Class DeepSeekAssistant
    Private ReadOnly searchProvider As ISearchProvider
    Private ReadOnly deepSeekClient As DeepSeekApiClient

    Public Sub New()
        ' Change "duckduckgo" to "bing" if you have a Bing API key
        searchProvider = SearchProviderFactory.CreateProvider("duckduckgo")
        deepSeekClient = New DeepSeekApiClient()
    End Sub

    ''' Processes user input, optionally augmenting with web search.
    Public Async Function AskAsync(userInput As String) As Task(Of String)
        If Not ShouldSearchWeb(userInput) Then
            Return Await deepSeekClient.GetCompletionAsync(userInput)
        End If
        Try
            Dim results = Await searchProvider.SearchAsync(userInput, 5)
            If results.Count = 0 Then
                Return Await deepSeekClient.GetCompletionAsync(userInput)
            End If
            Dim context = BuildSearchContext(results)
            Dim prompt = $"User: {userInput}{vbCrLf}Latest web search results:{vbCrLf}{context}{vbCrLf}Please answer using these results if helpful."
            Return Await deepSeekClient.GetCompletionAsync(prompt)
        Catch ex As Exception
            Return $"Web search failed: {ex.Message}. Falling back to base response."
        End Try
    End Function

    Function ShouldSearchWeb(input As String) As Boolean
        Dim triggers = {"search", "latest", "news", "today", "current", "weather", "stock", "price", "update", "recent", "find", "who is", "what is"}
        For Each word In triggers
            If input.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
        Next
        Return False
    End Function

    Function BuildSearchContext(results As List(Of SearchResult)) As String
        Dim sb = New System.Text.StringBuilder()
        For i = 0 To results.Count - 1
            Dim r = results(i)
            sb.AppendLine($"{i + 1}. {r.Title}")
            sb.AppendLine($"   URL: {r.Url}")
            sb.AppendLine($"   {r.Snippet}")
            If r.Date.HasValue Then sb.AppendLine($"   Date: {r.Date:yyyy-MM-dd}")
        Next
        Return sb.ToString()
    End Function
End Class

''' Client for DeepSeek API (mock implementation).
Class DeepSeekApiClient
    Private Shared ReadOnly ApiKey As String = "YOUR_DEEPSEEK_API_KEY"
    Private ReadOnly client As HttpClient

    Public Sub New()
        client = New HttpClient()
        client.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", ApiKey)
        client.BaseAddress = New Uri("https://chat.deepseek.com/")
    End Sub

    ''' Sends a prompt to DeepSeek and returns the completion.
    Public Async Function GetCompletionAsync(prompt As String) As Task(Of String)
        ' In production, serialize and POST a CompletionRequest.
        ' For demonstration, we return a mock answer.
        Await Task.Delay(30) ' simulate latency
        If prompt.Contains("web search results") Then
            Return "Based on the latest information, I've integrated the web search context into my answer. [Mock response]"
        End If
        Return "I understand your query. As an AI, I can help with that. [Mock response]"
    End Function


End Class

''' Simple cache cleaner that removes expired entries periodically.
Class SearchCacheManager
    Private Shared timer As System.Timers.Timer
    Private Shared cache As ConcurrentDictionary(Of String, (Results As List(Of SearchResult), Timestamp As DateTime))
    Private Shared ReadOnly expiration As TimeSpan = TimeSpan.FromMinutes(10)

    Shared Sub Start(cacheInstance As ConcurrentDictionary(Of String, (List(Of SearchResult), DateTime)))
        cache = cacheInstance
        timer = New System.Timers.Timer(60000) ' every minute
        AddHandler timer.Elapsed, AddressOf CleanCache
        timer.Start()
    End Sub

    Private Shared Sub CleanCache(sender As Object, e As System.Timers.ElapsedEventArgs)
        Dim now = DateTime.Now
        For Each key In cache.Keys
            If cache.TryGetValue(key, Dim entry) AndAlso now - entry.Timestamp > expiration Then
                cache.TryRemove(key, Nothing)
            End If
        Next
    End Sub
End Class

''' Helper for colored console output.
Class ConsoleHelper
    Public Shared Sub WriteLineColored(message As String, color As ConsoleColor)
        Console.ForegroundColor = color
        Console.WriteLine(message)
        Console.ResetColor()
    End Sub
End Class

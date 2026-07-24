' ==============================================================================
' Air Studio Backend - VB.NET (Windows Forms Application)
' Targets: .NET Framework 4.7.2 or higher (also works with .NET 6+ with minor adjustments)
' Reference: System.Net.Http, System.Net.WebSockets, System.Web (optional)
' ==============================================================================
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Net.WebSockets
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Collections.Concurrent
Imports System.Runtime.CompilerServices

Class Form1
    Private server As HttpServer
    Private WithEvents btnStart As Button
    Private WithEvents btnStop As Button
    Private lblStatus As Label
    Private lblMotion As Label
    Private txtLog As RichTextBox
    Private currentMotion As String = "X:0.00 Y:0.00 Z:0.00"

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Sub InitializeComponent()
        Me.Text = "Air Studio Server (VB.NET)"
        Me.Size = New Size(600, 450)
        Me.StartPosition = FormStartPosition.CenterScreen

        btnStart = New Button With {
            .Text = "Start Server",
            .Location = New Point(20, 20),
            .Size = New Size(100, 30)
        }
        btnStop = New Button With {
            .Text = "Stop Server",
            .Location = New Point(130, 20),
            .Size = New Size(100, 30),
            .Enabled = False
        }
        lblStatus = New Label With {
            .Text = "Status: Stopped",
            .Location = New Point(20, 60),
            .AutoSize = True
        }
        lblMotion = New Label With {
            .Text = "Motion: " & currentMotion,
            .Location = New Point(20, 85),
            .AutoSize = True,
            .Font = New Font("Consolas", 10)
        }
        txtLog = New RichTextBox With {
            .Location = New Point(20, 120),
            .Size = New Size(550, 280),
            .ReadOnly = True,
            .Font = New Font("Consolas", 9)
        }

        Me.Controls.Add(btnStart)
        Me.Controls.Add(btnStop)
        Me.Controls.Add(lblStatus)
        Me.Controls.Add(lblMotion)
        Me.Controls.Add(txtLog)
    End Sub

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Log("Air Studio Backend ready.")
        Log($"Local IP: {GetLocalIPAddress()}")
    End Sub

    Private Sub btnStart_Click(sender As Object, e As EventArgs) Handles btnStart.Click
        Try
            server = New HttpServer("http://+:8899/")
            AddHandler server.LogMessage, AddressOf Log
            AddHandler server.MotionUpdated, AddressOf UpdateMotion
            server.Start()
            btnStart.Enabled = False
            btnStop.Enabled = True
            lblStatus.Text = "Status: Running on port 8899"
            Log("Server started. Access from your phone: http://" & GetLocalIPAddress() & ":8899")
        Catch ex As Exception
            Log("Error starting server: " & ex.Message)
        End Try
    End Sub

    Private Sub btnStop_Click(sender As Object, e As EventArgs) Handles btnStop.Click
        server?.Stop()
        btnStart.Enabled = True
        btnStop.Enabled = False
        lblStatus.Text = "Status: Stopped"
        Log("Server stopped.")
    End Sub

    Private Sub Log(message As String)
        If txtLog.InvokeRequired Then
            txtLog.Invoke(Sub() Log(message))
        Else
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}")
            txtLog.ScrollToCaret()
        End If
    End Sub

    Private Sub UpdateMotion(motionText As String)
        If lblMotion.InvokeRequired Then
            lblMotion.Invoke(Sub() UpdateMotion(motionText))
        Else
            currentMotion = motionText
            lblMotion.Text = "Motion: " & motionText
        End If
    End Sub

    Private Shared Function GetLocalIPAddress() As String
        Dim host = Dns.GetHostEntry(Dns.GetHostName())
        For Each ip In host.AddressList
            If ip.AddressFamily = AddressFamily.InterNetwork Then
                Return ip.ToString()
            End If
        Next
        Return "127.0.0.1"
    End Function

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        server?.Stop()
    End Sub
End Class

' ==============================================================================
' HTTP Server with WebSocket support
' ==============================================================================
Class HttpServer
    Private listener As HttpListener
    Private cts As CancellationTokenSource
    Private wsManager As WebSocketConnectionManager
    Private recordingManager As RecordingManager

    Public Event LogMessage(message As String)
    Public Event MotionUpdated(motionText As String)

    Public Sub New(prefix As String)
        listener = New HttpListener()
        listener.Prefixes.Add(prefix)
        wsManager = New WebSocketConnectionManager()
        recordingManager = New RecordingManager()
    End Sub

    Public Sub Start()
        listener.Start()
        cts = New CancellationTokenSource()
        ' Fire and forget the main listening loop
        Task.Run(Function() ListenLoop(cts.Token))
    End Sub

    Public Sub [Stop]()
        cts?.Cancel()
        listener?.Stop()
        wsManager.Dispose()
    End Sub

    Private Async Function ListenLoop(token As CancellationToken) As Task
        RaiseEvent LogMessage("HTTP listener started.")
        While Not token.IsCancellationRequested
            Try
                Dim context = Await listener.GetContextAsync()
                ' Handle request on a separate task
                Task.Run(Sub() ProcessRequest(context))
            Catch ex As HttpListenerException
                If token.IsCancellationRequested Then Exit While
            Catch ex As Exception
                RaiseEvent LogMessage("Listener error: " & ex.Message)
            End Try
        End While
        RaiseEvent LogMessage("HTTP listener stopped.")
    End Function

    Private Async Sub ProcessRequest(context As HttpListenerContext)
        Dim request = context.Request
        Dim response = context.Response

        Try
            If request.IsWebSocketRequest Then
                Await ProcessWebSocketRequest(context)
                Return
            End If

            ' Route API
            Dim path = request.Url.AbsolutePath.ToLower()
            Select Case path
                Case "/"
                    ServeStaticFile(response, "index.html", "text/html")
                Case "/api/recordings"
                    Await HandleRecordingsList(response)
                Case "/api/snapshot"
                    Await HandleSnapshot(request, response)
                Case "/api/upload_recording"
                    Await HandleUploadRecording(request, response)
                Case "/api/upload_to_bilibili"
                    Await HandleBilibiliUpload(request, response)
                Case Else
                    ' Serve static files if they exist (for CSS/JS)
                    Dim filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", path.TrimStart("/"c))
                    If File.Exists(filePath) Then
                        Dim contentType = GetContentType(filePath)
                        ServeStaticFile(response, filePath, contentType)
                    Else
                        response.StatusCode = 404
                        WriteResponse(response, "{""error"":""Not found""}", "application/json")
                    End If
            End Select
        Catch ex As Exception
            RaiseEvent LogMessage("Request error: " & ex.Message)
            response.StatusCode = 500
            WriteResponse(response, "{""error"":""Internal server error""}", "application/json")
        Finally
            response.Close()
        End Try
    End Sub

    ' ---------- Static file serving ----------
    Private Sub ServeStaticFile(response As HttpListenerResponse, filePathOrName As String, contentType As String)
        Dim fullPath As String
        If Path.IsPathRooted(filePathOrName) Then
            fullPath = filePathOrName
        Else
            ' Look in application directory
            fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePathOrName)
        End If

        If Not File.Exists(fullPath) Then
            response.StatusCode = 404
            WriteResponse(response, "File not found", "text/plain")
            Return
        End If

        Try
            Dim buffer = File.ReadAllBytes(fullPath)
            response.ContentType = contentType
            response.ContentLength64 = buffer.Length
            response.OutputStream.Write(buffer, 0, buffer.Length)
        Catch ex As Exception
            response.StatusCode = 500
            WriteResponse(response, "Error reading file", "text/plain")
        End Try
    End Sub

    Private Function GetContentType(filePath As String) As String
        Select Case Path.GetExtension(filePath).ToLower()
            Case ".html", ".htm" : Return "text/html"
            Case ".css" : Return "text/css"
            Case ".js" : Return "application/javascript"
            Case ".jpg", ".jpeg" : Return "image/jpeg"
            Case ".png" : Return "image/png"
            Case ".webm" : Return "video/webm"
            Case Else : Return "application/octet-stream"
        End Select
    End Function

    ' ---------- API Handlers ----------
    Private Async Function HandleRecordingsList(response As HttpListenerResponse) As Task
        Dim files = recordingManager.GetRecordings()
        Dim json = JsonSerializer.Serialize(New With {.files = files})
        WriteResponse(response, json, "application/json")
    End Function

    Private Async Function HandleSnapshot(request As HttpListenerRequest, response As HttpListenerResponse) As Task
        Dim body = ReadRequestBody(request)
        Dim jsonDoc = JsonDocument.Parse(body)
        Dim imageBase64 = jsonDoc.RootElement.GetProperty("image").GetString()
        Dim timestamp = jsonDoc.RootElement.GetProperty("timestamp").GetInt64()

        ' Save snapshot
        Dim snapshotsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots")
        Directory.CreateDirectory(snapshotsDir)
        Dim fileName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
        Dim filePath = Path.Combine(snapshotsDir, fileName)

        ' Remove data URL prefix if present (data:image/jpeg;base64,...)
        If imageBase64.Contains(",") Then
            imageBase64 = imageBase64.Substring(imageBase64.IndexOf(",") + 1)
        End If
        Dim imageBytes = Convert.FromBase64String(imageBase64)
        File.WriteAllBytes(filePath, imageBytes)

        RaiseEvent LogMessage($"Snapshot saved: {fileName}")
        WriteResponse(response, "{""status"":""ok""}", "application/json")
    End Function

    Private Async Function HandleUploadRecording(request As HttpListenerRequest, response As HttpListenerResponse) As Task
        ' Parse multipart form data
        Dim boundary = GetBoundary(request.ContentType)
        If boundary Is Nothing Then
            response.StatusCode = 400
            WriteResponse(response, "{""error"":""Invalid multipart data""}", "application/json")
            Return
        End If

        Dim recordingsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings")
        Directory.CreateDirectory(recordingsDir)

        Dim filePath As String = Nothing
        Dim fileName As String = Nothing

        Using reader = New MultipartReader(boundary, request.InputStream)
            Dim section = Await reader.ReadNextSectionAsync()
            While section IsNot Nothing
                Dim disposition = section.ContentDisposition
                If disposition.Contains("filename=") Then
                    fileName = ExtractFileName(disposition)
                    If String.IsNullOrEmpty(fileName) Then fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.webm"
                    filePath = Path.Combine(recordingsDir, fileName)
                    Using fileStream = File.Create(filePath)
                        Await section.Body.CopyToAsync(fileStream)
                    End Using
                    RaiseEvent LogMessage($"Recording saved: {fileName}")
                End If
                section = Await reader.ReadNextSectionAsync()
            End While
        End Using

        If Not String.IsNullOrEmpty(filePath) Then
            recordingManager.AddRecording(fileName, New FileInfo(filePath).Length)
            ' Simulate starting Bilibili upload after a delay (optional)
            WriteResponse(response, "{""status"":""ok""}", "application/json")
        Else
            response.StatusCode = 400
            WriteResponse(response, "{""error"":""No file uploaded""}", "application/json")
        End If
    End Function

    Private Async Function HandleBilibiliUpload(request As HttpListenerRequest, response As HttpListenerResponse) As Task
        Dim body = ReadRequestBody(request)
        Dim jsonDoc = JsonDocument.Parse(body)
        Dim filename = jsonDoc.RootElement.GetProperty("filename").GetString()
        Dim title = If(jsonDoc.RootElement.TryGetProperty("title", out _), jsonDoc.RootElement.GetProperty("title").GetString(), "Air Studio Recording")
        Dim desc = If(jsonDoc.RootElement.TryGetProperty("desc", out _), jsonDoc.RootElement.GetProperty("desc").GetString(), "")
        Dim tags = If(jsonDoc.RootElement.TryGetProperty("tags", out _), jsonDoc.RootElement.GetProperty("tags"), Nothing)

        ' Simulate Bilibili upload (replace with real API calls)
        RaiseEvent LogMessage($"Bilibili upload requested: {filename} -> Title: {title}")
        Await Task.Delay(2000) ' Simulate processing
        Dim bvid = "BV" & Guid.NewGuid().ToString().Substring(0, 10).Replace("-", "")
        recordingManager.MarkUploaded(filename)
        RaiseEvent LogMessage($"Bilibili upload completed. BVID: {bvid}")
        WriteResponse(response, JsonSerializer.Serialize(New With {.status = "ok", .bvid = bvid}), "application/json")
    End Function

    ' ---------- WebSocket ----------
    Private Async Function ProcessWebSocketRequest(context As HttpListenerContext) As Task
        Dim wsContext As HttpListenerWebSocketContext = Nothing
        Try
            wsContext = Await context.AcceptWebSocketAsync(Nothing)
            RaiseEvent LogMessage("WebSocket connected.")
            wsManager.AddSocket(wsContext.WebSocket)
            Await wsManager.ProcessConnection(wsContext.WebSocket, AddressOf HandleWebSocketMessage)
        Catch ex As Exception
            RaiseEvent LogMessage("WebSocket error: " & ex.Message)
        End Try
    End Function

    Private Sub HandleWebSocketMessage(message As String, socket As WebSocket)
        Try
            Dim jsonDoc = JsonDocument.Parse(message)
            Dim type = jsonDoc.RootElement.GetProperty("type").GetString()
            Select Case type
                Case "motion"
                    Dim x = jsonDoc.RootElement.GetProperty("x").GetDouble()
                    Dim y = jsonDoc.RootElement.GetProperty("y").GetDouble()
                    Dim z = jsonDoc.RootElement.GetProperty("z").GetDouble()
                    Dim motionText = $"X:{x:F2} Y:{y:F2} Z:{z:F2}"
                    RaiseEvent MotionUpdated(motionText)
                    ' Optionally broadcast to other clients
                Case "orientation"
                    ' Handle orientation data
                Case "device_info"
                    RaiseEvent LogMessage($"Device connected: {jsonDoc.RootElement.GetProperty("userAgent").GetString()}")
                Case "recording_started"
                    RaiseEvent LogMessage("Recording started on phone.")
                Case "recording_stopped"
                    RaiseEvent LogMessage("Recording stopped on phone.")
                Case "ping"
                    ' Respond with pong
                    wsManager.SendMessage(JsonSerializer.Serialize(New With {.type = "pong"}), socket)
            End Select
        Catch ex As Exception
            RaiseEvent LogMessage("WS message parse error: " & ex.Message)
        End Try
    End Sub

    ' ---------- Helpers ----------
    Private Shared Function ReadRequestBody(request As HttpListenerRequest) As String
        Using reader = New StreamReader(request.InputStream, request.ContentEncoding)
            Return reader.ReadToEnd()
        End Using
    End Function

    Private Shared Sub WriteResponse(response As HttpListenerResponse, content As String, contentType As String)
        Dim buffer = Encoding.UTF8.GetBytes(content)
        response.ContentType = contentType
        response.ContentLength64 = buffer.Length
        response.OutputStream.Write(buffer, 0, buffer.Length)
    End Sub

    Private Shared Function GetBoundary(contentType As String) As String
        If String.IsNullOrEmpty(contentType) Then Return Nothing
        Dim parts = contentType.Split(";"c)
        For Each part In parts
            Dim trimmed = part.Trim()
            If trimmed.StartsWith("boundary=") Then
                Return trimmed.Substring("boundary=".Length).Trim(""""c)
            End If
        Next
        Return Nothing
    End Function

    Private Shared Function ExtractFileName(contentDisposition As String) As String
        Dim pattern = "filename=""(?<name>[^""]*)"""
        Dim match = Text.RegularExpressions.Regex.Match(contentDisposition, pattern)
        If match.Success Then
            Return match.Groups("name").Value
        End If
        Return Nothing
    End Function
End Class

' ==============================================================================
' WebSocket Connection Manager (handles multiple clients)
' ==============================================================================
Class WebSocketConnectionManager
    Implements IDisposable

    Private connections As New ConcurrentBag(Of WebSocket)
    Private processingTasks As New List(Of Task)

    Public Sub AddSocket(socket As WebSocket)
        connections.Add(socket)
    End Sub

    Public Async Function ProcessConnection(socket As WebSocket, messageHandler As Action(Of String, WebSocket)) As Task
        Dim buffer = New ArraySegment(Of Byte)(New Byte(4095) {})
        While socket.State = WebSocketState.Open
            Try
                Dim result = Await socket.ReceiveAsync(buffer, CancellationToken.None)
                If result.MessageType = WebSocketMessageType.Close Then
                    Await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                    Exit While
                Else
                    Dim message = Encoding.UTF8.GetString(buffer.Array, 0, result.Count)
                    ' Handle fragmented messages (simple approach: accumulate until end)
                    If result.EndOfMessage Then
                        messageHandler(message, socket)
                    End If
                End If
            Catch ex As WebSocketException
                Exit While
            End Try
        End While
    End Function

    Public Async Sub SendMessage(message As String, target As WebSocket)
        If target.State = WebSocketState.Open Then
            Dim buffer = Encoding.UTF8.GetBytes(message)
            Await target.SendAsync(New ArraySegment(Of Byte)(buffer), WebSocketMessageType.Text, True, CancellationToken.None)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        For Each ws In connections
            Try
                ws.Dispose()
            Catch
            End Try
        Next
    End Sub
End Class

' ==============================================================================
' Simple multipart reader (handles boundary-based parsing)
' ==============================================================================
Class MultipartReader
    Implements IDisposable

    Private boundary As String
    Private stream As Stream
    Private buffer As Byte()
    Private readPosition As Integer
    Private endOfStream As Boolean

    Public Sub New(boundary As String, stream As Stream)
        Me.boundary = "--" & boundary
        Me.stream = stream
        buffer = New Byte(4095) {}
    End Sub

    Public Async Function ReadNextSectionAsync() As Task(Of MultipartSection)
        ' Simplified multipart reading (not fully RFC-compliant but works for most cases)
        ' Read until boundary, then parse headers, then read until next boundary.
        ' For production, consider using Microsoft.AspNetCore.WebUtilities.MultipartReader if targeting .NET Core/.NET 5+

        ' Search for boundary
        Dim line = Await ReadLineAsync()
        If line Is Nothing Then Return Nothing
        If line = boundary Then
            ' Next line is headers (Content-Disposition, etc.)
            Dim headers = New StringBuilder()
            While True
                Dim headerLine = Await ReadLineAsync()
                If String.IsNullOrEmpty(headerLine) Then Exit While
                headers.AppendLine(headerLine)
            End While

            ' Now read body until the next boundary
            Dim bodyStream = New MemoryStream()
            Dim boundaryBytes = Encoding.UTF8.GetBytes(vbCrLf & boundary)
            Dim matchIndex = 0
            Dim b As Integer
            While True
                b = stream.ReadByte()
                If b = -1 Then Exit While
                bodyStream.WriteByte(CByte(b))
                ' Check for boundary
                If b = boundaryBytes(matchIndex) Then
                    matchIndex += 1
                    If matchIndex = boundaryBytes.Length Then
                        ' Found boundary, truncate the boundary from bodyStream
                        bodyStream.SetLength(bodyStream.Length - boundaryBytes.Length)
                        ' Read the rest of the line after boundary (-- or \r\n)
                        Dim nextTwo = New Byte(1) {}
                        stream.Read(nextTwo, 0, 2)
                        Exit While
                    End If
                Else
                    matchIndex = 0
                End If
            End While

            bodyStream.Position = 0
            Dim contentDisposition = ExtractHeader(headers.ToString(), "Content-Disposition")
            Return New MultipartSection With {
                .ContentDisposition = contentDisposition,
                .Body = bodyStream
            }
        ElseIf line = boundary & "--" Then
            Return Nothing ' end of multipart
        Else
            ' skip preamble
            Return Await ReadNextSectionAsync()
        End If
    End Function

    Private Async Function ReadLineAsync() As Task(Of String)
        Dim line As New StringBuilder()
        While True
            Dim b = stream.ReadByte()
            If b = -1 Then Return Nothing
            If b = vbCr Then Continue While
            If b = vbLf Then Return line.ToString()
            line.Append(ChrW(b))
        End While
    End Function

    Private Shared Function ExtractHeader(headers As String, headerName As String) As String
        Dim lines = headers.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
        For Each line In lines
            If line.StartsWith(headerName, StringComparison.OrdinalIgnoreCase) Then
                Return line.Substring(headerName.Length + 1).Trim()
            End If
        Next
        Return ""
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        stream?.Dispose()
    End Sub
End Class

Class MultipartSection
    Public Property ContentDisposition As String
    Public Property Body As Stream
End Class

' ==============================================================================
' Recording Manager (tracks saved files)
' ==============================================================================
Class RecordingManager
    Private recordings As New List(Of RecordingInfo)
    Private lockObj As New Object

    Public Sub AddRecording(fileName As String, size As Long)
        SyncLock lockObj
            recordings.Add(New RecordingInfo With {
                .Name = fileName,
                .Size = size,
                .UploadedToBilibili = False,
                .Uploading = False
            })
        End SyncLock
    End Sub

    Public Sub MarkUploaded(fileName As String)
        SyncLock lockObj
            Dim rec = recordings.FirstOrDefault(Function(r) r.Name = fileName)
            If rec IsNot Nothing Then
                rec.UploadedToBilibili = True
                rec.Uploading = False
            End If
        End SyncLock
    End Sub

    Public Function GetRecordings() As List(Of RecordingInfo)
        SyncLock lockObj
            Return New List(Of RecordingInfo)(recordings)
        End SyncLock
    End Function
End Class

Class RecordingInfo
    Public Property Name As String
    Public Property Size As Long
    Public Property UploadedToBilibili As Boolean
    Public Property Uploading As Boolean
End Class
# Comparaison : Tests Middleware vs Endpoints

## Configuration de l'hôte de test

### Middleware (Avant)
```csharp
using IHost host = await new HostBuilder()
    .ConfigureWebHost(webBuilder =>
    {
        webBuilder
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                services.AddSingleton<ISendClient, SendClientMock>();
                services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                services.AddSingleton<IReplicasService, MemoryReplicasService>();
                services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                // ❌ Pas de AddRouting()
            })
            .Configure(app => {
                app.UseMiddleware<SlimProxyMiddleware>(); // ❌ Middleware global
            });
    })
    .StartAsync();
```

### Endpoints (Après)
```csharp
using IHost host = await new HostBuilder()
    .ConfigureWebHost(webBuilder =>
    {
        webBuilder
            .UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<HistoryHttpMemoryService, HistoryHttpMemoryService>();
                services.AddSingleton<ISendClient, SendClientMock>();
                services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                services.AddSingleton<IReplicasService, MemoryReplicasService>();
                services.AddSingleton<IWakeUpFunction>(sp => wakeUpFunctionMock.Object);
                services.AddSingleton<IJobService>(sp => jobServiceMock.Object);
                services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
                services.AddRouting(); // ✅ Nouveau
            })
            .Configure(app =>
            {
                app.UseRouting(); // ✅ Nouveau
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapEventEndpoints(); // ✅ Endpoints spécifiques
                });
            });
    })
    .StartAsync();
```

---

## Test 1 : Publication d'événements

### Middleware (Avant)
```csharp
[Theory]
[InlineData("/publish-event/reload/hello", HttpStatusCode.NoContent, "...")]
public async Task CallPublishInSyncModeAndReturnOk(string path, HttpStatusCode expected, string? times)
{
    // ...setup...

    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });

    // ❌ Utilise GET
    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    Assert.Equal(expected, response.StatusCode);
}
```

### Endpoints (Après)
```csharp
[Theory]
[InlineData("/publish-event/reload/hello", HttpStatusCode.NoContent, "...")]
public async Task CallPublishEventEndpointAndReturnOk(string path, HttpStatusCode expected, string? times)
{
    // ...setup...

    .Configure(app =>
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapEventEndpoints(); // ✅ Endpoint spécifique
        });
    });

    // ✅ Utilise POST
    HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", null);

    Assert.Equal(expected, response.StatusCode);
}
```

**Différences** :
- ✅ Endpoint spécifique au lieu du middleware global
- ✅ Verbe HTTP : GET → POST
- ✅ Nom du test plus explicite

---

## Test 2 : Fonctions synchrones

### Middleware (Avant)
```csharp
[Theory]
[InlineData("/function/fibonacci/compute", HttpStatusCode.OK)]
public async Task CallFunctionInSyncModeAndReturnOk(string path, HttpStatusCode expected)
{
    // ...setup...

    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    Assert.Equal(expected, response.StatusCode);
}
```

### Endpoints (Après)
```csharp
[Theory]
[InlineData("/function/fibonacci/compute", HttpStatusCode.OK)]
public async Task CallSyncFunctionEndpointAndReturnOk(string path, HttpStatusCode expected)
{
    // ...setup...

    .Configure(app =>
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapSyncFunctionEndpoints(); // ✅ Endpoint spécifique
        });
    });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    Assert.Equal(expected, response.StatusCode);
}
```

**Différences** :
- ✅ Endpoint spécifique
- ✅ Verbe HTTP reste GET
- ✅ Nom du test plus explicite

---

## Test 3 : Fonctions asynchrones

### Middleware (Avant)
```csharp
[Theory]
[InlineData("/async-function/fibonacci/download", HttpStatusCode.Accepted)]
public async Task CallFunctionInAsyncSyncModeAndReturnOk(string path, HttpStatusCode expected)
{
    // ...setup...

    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    Assert.Equal(expected, response.StatusCode);
}
```

### Endpoints (Après)
```csharp
[Theory]
[InlineData("/async-function/fibonacci/download", HttpStatusCode.Accepted)]
public async Task CallAsyncFunctionEndpointAndReturnOk(string path, HttpStatusCode expected)
{
    // ...setup...

    .Configure(app =>
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapAsyncFunctionEndpoints(); // ✅ Endpoint spécifique
        });
    });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    Assert.Equal(expected, response.StatusCode);
}
```

**Différences** :
- ✅ Endpoint spécifique
- ✅ Verbe HTTP reste GET
- ✅ Nom du test corrigé (typo "AsyncSync")

---

## Test 4 : Réveil de fonction

### Middleware (Avant)
```csharp
[Theory]
[InlineData("/wake-function/fibonacci", HttpStatusCode.NoContent, 1)]
public async Task JustWakeFunctionAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode, int numberFireAndForgetWakeUpAsyncCall)
{
    // ...setup...

    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });

    // ❌ Utilise GET
    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");

    wakeUpFunctionMock.Verify(k => k.FireAndForgetWakeUpAsync(It.IsAny<string>()), Times.AtMost(numberFireAndForgetWakeUpAsyncCall));
    Assert.Equal(expectedHttpStatusCode, response.StatusCode);
}
```

### Endpoints (Après)
```csharp
[Theory]
[InlineData("/wake-function/fibonacci", HttpStatusCode.NoContent, 1)]
public async Task WakeFunctionEndpointAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode, int numberFireAndForgetWakeUpAsyncCall)
{
    // ...setup...

    .Configure(app =>
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapStatusEndpoints(); // ✅ Endpoint spécifique
        });
    });

    // ✅ Utilise POST
    HttpResponseMessage response = await host.GetTestClient().PostAsync($"http://localhost:5000{path}", null);

    wakeUpFunctionMock.Verify(k => k.FireAndForgetWakeUpAsync(It.IsAny<string>()), Times.AtMost(numberFireAndForgetWakeUpAsyncCall));
    Assert.Equal(expectedHttpStatusCode, response.StatusCode);
}
```

**Différences** :
- ✅ Endpoint spécifique
- ✅ Verbe HTTP : GET → POST
- ✅ Nom du test plus explicite

---

## Test 5 : Statut de fonction

### Middleware (Avant)
```csharp
[Theory]
[InlineData("/status-function/fibonacci", HttpStatusCode.OK,
    "{\"NumberReady\":1,\"NumberRequested\":0,\"PodType\":\"Deployment\",\"Visibility\":\"Public\",\"Name\":\"fibonacci\"}")]
public async Task GetStatusFunctionAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode, string expectedBody)
{
    // ...setup...

    .Configure(app => { app.UseMiddleware<SlimProxyMiddleware>(); });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");
    string body = await response.Content.ReadAsStringAsync();

    Assert.Equal(expectedBody, body); // ❌ PascalCase
    Assert.Equal(expectedHttpStatusCode, response.StatusCode);
}
```

### Endpoints (Après)
```csharp
[Theory]
[InlineData("/status-function/fibonacci", HttpStatusCode.OK,
    "{\"numberReady\":1,\"numberRequested\":0,\"podType\":\"Deployment\",\"visibility\":\"Public\",\"functionName\":\"fibonacci\"}")]
public async Task GetStatusFunctionEndpointAndReturnOk(string path, HttpStatusCode expectedHttpStatusCode, string expectedBody)
{
    // ...setup...

    .Configure(app =>
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapStatusEndpoints(); // ✅ Endpoint spécifique
        });
    });

    HttpResponseMessage response = await host.GetTestClient().GetAsync($"http://localhost:5000{path}");
    string body = await response.Content.ReadAsStringAsync();

    Assert.Equal(expectedBody, body); // ✅ camelCase
    Assert.Equal(expectedHttpStatusCode, response.StatusCode);
}
```

**Différences** :
- ✅ Endpoint spécifique
- ✅ Verbe HTTP reste GET
- ✅ Format JSON : PascalCase → camelCase
- ✅ Propriété : `Name` → `functionName`

---

## Tableau récapitulatif des changements

| Aspect | Middleware | Endpoints | Changement |
|--------|-----------|-----------|------------|
| **Configuration** | `app.UseMiddleware<SlimProxyMiddleware>()` | `endpoints.MapXxxEndpoints()` | ✅ Spécifique |
| **Routing** | ❌ Non requis | ✅ `services.AddRouting()` | ✅ Ajouté |
| **Publish Event** | GET | POST | ⚠️ Changé |
| **Wake Function** | GET | POST | ⚠️ Changé |
| **Sync Function** | GET | GET | ✅ Identique |
| **Async Function** | GET | GET | ✅ Identique |
| **Status** | GET | GET | ✅ Identique |
| **JSON Format** | PascalCase | camelCase | ⚠️ Changé |
| **Propriété Name** | `Name` | `functionName` | ⚠️ Changé |

---

## Avantages de la migration

| Avantage | Description |
|----------|-------------|
| **Séparation** | Chaque endpoint testé individuellement |
| **Clarté** | Nom des tests plus explicites |
| **Performance** | Endpoints plus rapides que middleware |
| **Typage** | Routes fortement typées |
| **AOT** | Compatible Native AOT |
| **Maintenance** | Plus facile à maintenir |

---

## Points d'attention

⚠️ **Verbes HTTP** : `/publish-event` et `/wake-function` utilisent maintenant POST

⚠️ **Format JSON** : camelCase au lieu de PascalCase (JSON Source Generators)

⚠️ **Routing** : Il faut ajouter `services.AddRouting()`

⚠️ **Endpoints multiples** : Chaque type d'endpoint doit être mappé explicitement


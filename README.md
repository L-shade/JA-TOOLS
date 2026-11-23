# JA para Revit 2.0

[![Verificar e compilar](https://github.com/L-shade/JA-TOOLS/actions/workflows/compilar.yml/badge.svg)](https://github.com/L-shade/JA-TOOLS/actions/workflows/compilar.yml)

Rotina de trabalho (verificação, compilação automática e publicação no GitHub): [docs/ROTINA.md](docs/ROTINA.md).

Add-in do Revit 2024–2027 da **JA Arquitetos Associados**. Uma só instalação e uma só DLL
(`JA.Revit.dll`), com as duas abas de sempre:

- **JA Famílias** — esquadrias, perfis, pedras e nichos, revestimentos (paginação, rodapé, forro e
  sanca, impermeabilização, escada, ralos), vidros e madeira, documentação de obra.
- **JA Tools** — DWG/PDF/pranchas, vistas, cotas, parâmetros e conferências, modelagem por
  ambiente, Central JA.

A Central JA, o Buscar Comando e a Ajuda mostram as ferramentas das duas abas.

## Instalar

1. Feche o Revit.
2. Rode `JA_Setup_<versão>.exe` (ou `Instalar tudo.bat` do pacote do escritório).
3. O instalador remove sozinho o JA Tools e o JA Famílias antigos; as opções salvas e as
   configurações continuam valendo.

## Compilar (desenvolvimento)

- `CompilarTeste.bat` — compila para o Revit 2024–2027 (pasta dist) e grava `build-log.txt`.
  Se der erro, mande esse log.
- `GerarInstalador.bat` — gera `JA_Setup_<versão>.exe` (precisa do Inno Setup 6).
- `GerarPacoteEscritorio.bat` — gera o zip para a pasta da rede.

## Estrutura

```
JA.sln
src/JA.addin                  manifesto único (duas <AddIn>, uma por aba)
src/JA.Revit/                 o add-in
  JAFamilias/                 aba JA Famílias (namespace JAEsquadrias)
  JATools/                    aba JA Tools e a base comum (namespace RevitDwgUnifier)
  Comum/GlobalUsings.cs       nomes comuns às duas partes
src/Shared, src/RevitDwgUnifier.AcadMerge   motor de mesclagem DWG (AutoCAD)
installer/JA.iss              instalador único
content/                      famílias e identificadores instalados junto
docs/                         README antigos de cada aba, testes
MEMORIA.md                    histórico de pedidos e decisões
```

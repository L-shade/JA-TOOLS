"""Cenários escritos pelo QA (iteração 1, PR #7), adotados no repositório para o placar. Mesma importação do test_consenso.py.
O esperado é a leitura do QA de docs/equipe/PROCESSO.md; onde a regra é ambígua, está marcado 'AMBIGUO'."""
import os, sys
_aqui = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.join(_aqui, '..', '..', '..', 'tools', 'equipe'), _aqui):
    if os.path.exists(os.path.join(_p, 'consenso.py')): sys.path.insert(0, os.path.abspath(_p)); break
try:
    from consenso import decidir
except ImportError as e:
    print('NÃO VERIFICADO: o pacote não traz o placar (', e, ')')
    sys.exit(2)


def qa(i, c, v, n): return f"<!-- JA-QA\niteracao: {i}\ncommit: {c}\nveredito: {v}\nnota: {n}\n-->"
def pg(i, c, p): return f"<!-- JA-PROGRAMADOR\niteracao: {i}\ncommit: {c}\nposicao: {p}\n-->"
def en(i, c): return f"<!-- JA-ENTREGA\niteracao: {i}\ncommit: {c}\n-->"
def di(d, c=''): return f"<!-- JA-DIRETOR\ndecisao: {d}\n" + (f"commit: {c}\n" if c else '') + "-->"


A, B, C = 'aaaaaaa1111', 'bbbbbbb2222', 'ccccccc3333'
casos = [
    # modelo do PROCESSO.md copiado ao pé da letra, com o comentário entre parênteses
    ('modelo copiado com "(ou REPROVADO)"', [qa(1, A, 'APROVADO        (ou REPROVADO)', 90), pg(1, A, 'CONCORDO         (ou DISCORDO)')], 'LIBERAR', A),
    # regra 3: APROVADO exige nota >= 80
    ('APROVADO com nota 70 não libera', [qa(1, A, 'APROVADO', 70), pg(1, A, 'CONCORDO')], 'NOVA_ITERACAO', A),
    # fluxo: na 3ª iteração o consenso ainda é possível
    ('consenso na 3', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 60), pg(2, B, 'CONCORDO'),
                       qa(3, C, 'APROVADO', 82), pg(3, C, 'CONCORDO')], 'LIBERAR', C),
    # grave não limita a nota: REPROVADO 85 (com grave) x APROVADO 82
    ('melhor versão não deve passar REPROVADO na frente de APROVADO (AMBIGUO)',
     [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 85), pg(2, B, 'DISCORDO'),
      qa(3, C, 'APROVADO', 82), pg(3, C, 'DISCORDO')], 'MELHOR_VERSAO', C),
    # regra 2: posição de outro commit "não vale" -> continua aguardando o programador (AMBIGUO: test_consenso espera NOVA_ITERACAO)
    ('posição com commit errado não conta (AMBIGUO)', [qa(1, A, 'APROVADO', 90), pg(1, B, 'CONCORDO')], 'AGUARDANDO_PROGRAMADOR', A),
    ('diretor para e depois libera (AMBIGUO: o último vale?)', [qa(1, A, 'REPROVADO', 40), di('PARAR'), di('LIBERAR', A)], 'DIRETOR', A),
    ('iteração 4 é ignorada', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 75), pg(2, B, 'CONCORDO'),
                               qa(3, C, 'REPROVADO', 60), pg(3, C, 'CONCORDO'), qa(4, A, 'APROVADO', 99)], 'MELHOR_VERSAO', B),
    ('nota inválida não vira 100 nem quebra (iteração 3: bloco ignorado, vai para os avisos)', [qa(1, A, 'REPROVADO', 'noventa'), pg(1, A, 'CONCORDO')], 'AGUARDANDO_PROGRAMADOR', ''),
    ('commit curto demais (4) não casa', [qa(1, A, 'APROVADO', 90), pg(1, 'aaaa', 'CONCORDO')], 'AGUARDANDO_PROGRAMADOR', A),
    ('bloco citado (> ) não conta', [en(1, A), '> ' + qa(1, A, 'APROVADO', 90).replace('\n', '\n> ')], 'AGUARDANDO_QA', A),
]
ok = falhas = 0
for nome, coms, status, commit in casos:
    try:
        r = decidir(coms)
    except Exception as e:  # o placar não pode quebrar com comentário estranho
        falhas += 1; print('QUEBROU:', nome, '->', repr(e)); continue
    if r['status'] == status and (not commit or r['commit'] == commit.lower()):
        ok += 1
    else:
        falhas += 1; print('DIFERENTE:', nome, '->', r['status'], r.get('commit'), '| QA espera', status, commit)
print(f'{ok} ok, {falhas} diferente(s)')
sys.exit(1 if falhas else 0)

"""Cenários (2ª leva) escritos pelo QA na iteração 2 do PR #7, adotados; esperados ajustados onde a iteração 3 mudou a regra.
 do QA, iteração 2 (PR #7, commit f3a8ac6). Mesma importação do test_consenso.py.
'regra' = o esperado sai do PROCESSO.md; 'ambiguo' = o PROCESSO não diz; mostra o que o placar faz."""
import os, sys
_aqui = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.join(_aqui, '..', '..', '..', 'tools', 'equipe'), _aqui):
    if os.path.exists(os.path.join(_p, 'consenso.py')): sys.path.insert(0, os.path.abspath(_p)); break
from consenso import decidir, markdown


def bq(i, c, v, n, chave_it='iteracao'): return f"<!-- JA-QA\n{chave_it}: {i}\ncommit: {c}\nveredito: {v}\nnota: {n}\n-->"
def bp(i, c, p, chave_it='iteracao', chave_p='posicao'): return f"<!-- JA-PROGRAMADOR\n{chave_it}: {i}\ncommit: {c}\n{chave_p}: {p}\n-->"
def be(i, c): return f"<!-- JA-ENTREGA\niteracao: {i}\ncommit: {c}\n-->"
def bd(d, c=''): return f"<!-- JA-DIRETOR\ndecisao: {d}\n" + (f"commit: {c}\n" if c else '') + "-->"


A, B, C = 'aaaaaaa1111', 'bbbbbbb2222', 'ccccccc3333'
r3 = [bq(1, A, 'REPROVADO', 50), bp(1, A, 'CONCORDO'), bq(2, B, 'REPROVADO', 55), bp(2, B, 'CONCORDO')]
casos = [
    # --- regra
    ('regra', '"NÃO CONCORDO" não é concordar', [bq(1, A, 'APROVADO', 90), bp(1, A, 'NÃO CONCORDO')], ('NOVA_ITERACAO', 'AGUARDANDO_PROGRAMADOR'), A),
    ('regra', '"NÃO APROVADO" não é aprovar', [bq(1, A, 'NÃO APROVADO', 90), bp(1, A, 'CONCORDO')], ('NOVA_ITERACAO', 'AGUARDANDO_QA', 'AGUARDANDO_PROGRAMADOR'), ''),  # iteração 3: bloco inválido é ignorado (vai para os avisos)
    ('regra', '"CONCORDO EM PARTE" não libera', [bq(1, A, 'APROVADO', 90), bp(1, A, 'CONCORDO EM PARTE')], ('NOVA_ITERACAO', 'AGUARDANDO_PROGRAMADOR'), A),
    ('regra', 'prefixo de 6 não casa', [bq(1, A, 'APROVADO', 90), bp(1, A[:6], 'CONCORDO')], ('AGUARDANDO_PROGRAMADOR',), A),
    ('regra', 'QA com 7, programador com 11', [bq(1, A[:7], 'APROVADO', 90), bp(1, A, 'CONCORDO')], ('LIBERAR',), A[:7]),
    ('regra', 'commit em maiúsculas', [bq(1, A.upper(), 'APROVADO', 90), bp(1, A, 'CONCORDO')], ('LIBERAR',), A),
    ('regra', 'fim de linha do Windows (\\r\\n)', [bq(1, A, 'APROVADO', 90).replace('\n', '\r\n'), bp(1, A, 'CONCORDO').replace('\n', '\r\n')], ('LIBERAR',), A),
    ('regra', 'melhor versão: entre APROVADOS, a maior nota', [bq(1, A, 'REPROVADO', 50), bp(1, A, 'CONCORDO'), bq(2, B, 'APROVADO', 90), bp(2, B, 'DISCORDO'),
                                                               bq(3, C, 'APROVADO', 82), bp(3, C, 'DISCORDO')], ('MELHOR_VERSAO',), B),
    ('regra', 'melhor versão: APROVADO 75 vale REPROVADO 75', [bq(1, A, 'REPROVADO', 50), bp(1, A, 'CONCORDO'), bq(2, B, 'REPROVADO', 78), bp(2, B, 'CONCORDO'),
                                                              bq(3, C, 'APROVADO', 75), bp(3, C, 'CONCORDO')], ('MELHOR_VERSAO',), B),
    ('regra', 'diretor PARAR, RETOMAR, LIBERAR', [bq(1, A, 'REPROVADO', 40), bd('PARAR'), bd('RETOMAR'), bd('LIBERAR', A)], ('DIRETOR',), A),
    ('regra', 'diretor LIBERAR sem commit é ignorado (pergunta 3 do QA: evita liberar um push não avaliado)', [be(1, A), bq(1, A, 'REPROVADO', 40), bd('LIBERAR')], ('AGUARDANDO_PROGRAMADOR',), A),
    ('regra', 'bloco do diretor citado pelo gerente não conta', [be(1, A), '> ' + bd('PARAR').replace('\n', '\n> ')], ('AGUARDANDO_QA',), A),
    ('ambiguo', 'bloco em ~~~ não conta (o GitHub também trata ~~~ como código)', [be(1, A), '~~~\n' + bq(1, A, 'APROVADO', 90) + '\n~~~'], ('AGUARDANDO_QA',), A),
    ('regra', 'na 3, APROVADO + DISCORDO fecha: melhor versão', r3 + [bq(3, C, 'APROVADO', 95), bp(3, C, 'DISCORDO')], ('MELHOR_VERSAO',), C),
    ('regra', '"concordo com ressalvas" não libera', [bq(1, A, 'APROVADO', 90), bp(1, A, 'concordo com ressalvas')], ('NOVA_ITERACAO', 'AGUARDANDO_PROGRAMADOR'), A),
    ('regra', '"APROVADO COM RESSALVAS" não libera', [bq(1, A, 'APROVADO COM RESSALVAS', 90), bp(1, A, 'CONCORDO')], ('NOVA_ITERACAO', 'AGUARDANDO_QA', 'AGUARDANDO_PROGRAMADOR'), ''),  # iteração 3: bloco inválido é ignorado (vai para os avisos)
    ('regra', 'nota vazia não quebra o placar', [be(1, A), bq(1, A, 'APROVADO', '')], ('AGUARDANDO_PROGRAMADOR', 'AGUARDANDO_QA', 'NOVA_ITERACAO'), ''),
    ('regra', 'diretor: commit de 6 não vale (prefixo 7+)', [be(1, A), bd('LIBERAR', A[:6])], ('AGUARDANDO_QA',), ''),
    # --- ambiguo (o PROCESSO não diz)
    ('ambiguo', 'decisao "LIBERA" (erro de digitação) não deveria virar PARAR calado', [be(1, A), bd('LIBERA', A)], ('AGUARDANDO_QA',), ''),
    ('ambiguo', 'nota no formato do cabeçalho "86/100"', [bq(1, A, 'APROVADO', '86/100'), bp(1, A, 'CONCORDO')], ('LIBERAR',), A),
    ('ambiguo', 'chaves com acento (iteração/posição)', [bq(1, A, 'APROVADO', 90, 'iteração'), bp(1, A, 'CONCORDO', 'iteração', 'posição')], ('LIBERAR',), A),
    ('ambiguo', 'diretor PARAR e LIBERAR sem RETOMAR', [bq(1, A, 'REPROVADO', 40), bd('PARAR'), bd('LIBERAR', A)], ('DIRETOR',), A),
    ('ambiguo', 'QA pula da iteração 1 para a 3', [bq(1, A, 'REPROVADO', 50), bp(1, A, 'CONCORDO'), bq(3, C, 'REPROVADO', 60), bp(3, C, 'CONCORDO')], ('MELHOR_VERSAO', 'NOVA_ITERACAO'), ''),
    ('ambiguo', 'entrega 2 depois do consenso na 1', [bq(1, A, 'APROVADO', 90), bp(1, A, 'CONCORDO'), be(2, B)], ('LIBERAR',), A),
    ('ambiguo', 'bloco indentado (dentro de lista)', [be(1, A), '- relatório:\n  ' + bq(1, A, 'APROVADO', 90).replace('\n', '\n  '), bp(1, A, 'CONCORDO')], ('LIBERAR',), A),
    ('ambiguo', 'nota negativa', [bq(1, A, 'REPROVADO', -5), bp(1, A, 'CONCORDO')], ('NOVA_ITERACAO',), A),
    ('ambiguo', 'veredito "APROVADA"', [bq(1, A, 'APROVADA', 90), bp(1, A, 'CONCORDO')], ('NOVA_ITERACAO', 'AGUARDANDO_QA', 'AGUARDANDO_PROGRAMADOR'), ''),  # iteração 3: bloco inválido é ignorado (vai para os avisos)
]
ok = dif = 0
for tipo, nome, coms, esperado, commit in casos:
    try:
        r = decidir(coms); markdown(r)
    except Exception as e:
        dif += 1; print(f'QUEBROU  [{tipo}] {nome} -> {e!r}'); continue
    bate = r['status'] in esperado and (not commit or r['commit'] == commit.lower())
    ok += bate; dif += not bate
    if not bate: print(f"{'DIFERENTE'} [{tipo}] {nome} -> {r['status']} {r.get('commit')!r}" + ('' if bate else f'  | QA espera {esperado} {commit}'))
print(f'{ok} ok, {dif} diferente(s)')
sys.exit(1 if dif else 0)

"""Testes do placar da equipe (tools/equipe/consenso.py)."""
import os, sys
_aqui = os.path.dirname(os.path.abspath(__file__))
# no repositório: tools/equipe; no pacote do QA: a cópia ao lado deste arquivo
for _p in (os.path.join(_aqui, '..', '..', '..', 'tools', 'equipe'), _aqui):
    if os.path.exists(os.path.join(_p, 'consenso.py')): sys.path.insert(0, os.path.abspath(_p)); break
from consenso import decidir, blocos, markdown


def qa(i, c, v, n): return f"Relatório\n<!-- JA-QA\niteracao: {i}\ncommit: {c}\nveredito: {v}\nnota: {n}\n-->"
def pg(i, c, p): return f"<!-- JA-PROGRAMADOR\niteracao: {i}\ncommit: {c}\nposicao: {p}\n-->"
def en(i, c): return f"Entrega\n<!-- JA-ENTREGA\niteracao: {i}\ncommit: {c}\n-->"


A, B, C = 'aaaaaaa1111', 'bbbbbbb2222', 'ccccccc3333'
casos = [
    ('vazio', [], 'AGUARDANDO_PROGRAMADOR', ''),
    ('entregue, falta QA', [en(1, A)], 'AGUARDANDO_QA', A),
    ('posição antes do QA não conta', [en(1, A), pg(1, A, 'CONCORDO'), qa(1, A, 'APROVADO', 90)], 'AGUARDANDO_PROGRAMADOR', A),
    ('QA reavalia: posição antiga cai', [qa(1, A, 'REPROVADO', 40), pg(1, A, 'CONCORDO'), qa(1, A, 'APROVADO', 80)], 'AGUARDANDO_PROGRAMADOR', A),
    ('QA reprova, falta programador', [en(1, A), qa(1, A, 'REPROVADO', 40)], 'AGUARDANDO_PROGRAMADOR', A),
    ('consenso na 1', [qa(1, A, 'APROVADO', 90), pg(1, A[:7], 'CONCORDO')], 'LIBERAR', A),
    ('posição com commit errado não vale', [qa(1, A, 'APROVADO', 90), pg(1, B, 'CONCORDO')], 'AGUARDANDO_PROGRAMADOR', A),
    ('programador discorda do aprovado', [qa(1, A, 'APROVADO', 90), pg(1, A, 'DISCORDO')], 'NOVA_ITERACAO', A),
    ('consenso na 2', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'aprovado', '85,5'), pg(2, B, 'concordo')], 'LIBERAR', B),
    ('consenso na 3', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 60), pg(2, B, 'CONCORDO'),
                       qa(3, C, 'APROVADO', 82), pg(3, C, 'CONCORDO')], 'LIBERAR', C),
    ('na 3 espera a posição', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 55),
                               pg(2, B, 'CONCORDO'), qa(3, C, 'APROVADO', 95)], 'AGUARDANDO_PROGRAMADOR', C),
    ('3 sem consenso: maior nota', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 75), pg(2, B, 'DISCORDO'),
                                    qa(3, C, 'REPROVADO', 60), pg(3, C, 'DISCORDO')], 'MELHOR_VERSAO', B),
    ('empate: a mais nova', [qa(1, A, 'REPROVADO', 70), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 70), pg(2, B, 'CONCORDO'),
                             qa(3, C, 'REPROVADO', 70), pg(3, C, 'CONCORDO')], 'MELHOR_VERSAO', C),
    ('aprovado vence reprovado de nota maior', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'APROVADO', 81),
                                                pg(2, B, 'DISCORDO'), qa(3, C, 'REPROVADO', 79), pg(3, C, 'DISCORDO')], 'MELHOR_VERSAO', B),
    ('APROVADO com nota < 80 vale REPROVADO', [qa(1, A, 'APROVADO', 70), pg(1, A, 'CONCORDO')], 'NOVA_ITERACAO', A),
    ('iteração 4 não conta', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 55), pg(2, B, 'CONCORDO'),
                              qa(3, C, 'REPROVADO', 60), pg(3, C, 'CONCORDO'), qa(4, A, 'APROVADO', 99), pg(4, A, 'CONCORDO')], 'MELHOR_VERSAO', C),
    ('mesmo commit em duas iterações', [qa(1, A, 'REPROVADO', 60), pg(1, A, 'DISCORDO'), qa(2, A, 'APROVADO', 85), pg(2, A, 'CONCORDO')], 'LIBERAR', A),
    ('o último bloco vale', [qa(1, A, 'REPROVADO', 40), qa(1, A, 'APROVADO', 88), pg(1, A, 'CONCORDO')], 'LIBERAR', A),
    ('diretor libera', [qa(1, A, 'REPROVADO', 40), pg(1, A, 'DISCORDO'), f"<!-- JA-DIRETOR\ndecisao: LIBERAR\ncommit: {A}\n-->"], 'DIRETOR', A),
    ('diretor para', [qa(1, A, 'APROVADO', 99), "<!-- JA-DIRETOR\ndecisao: PARAR\n-->"], 'PARAR', ''),
    ('diretor retoma', [en(1, A), "<!-- JA-DIRETOR\ndecisao: PARAR\n-->", "<!-- JA-DIRETOR\ndecisao: RETOMAR\n-->"], 'AGUARDANDO_QA', A),
    ('texto sem bloco não conta', [en(1, A), 'JA-QA aprovado!'], 'AGUARDANDO_QA', A),
    ('bloco em ``` não conta', [en(1, A), '```\n' + qa(1, A, 'APROVADO', 90) + '\n```'], 'AGUARDANDO_QA', A),
    ('bloco citado (> ) não conta', [en(1, A), '\n'.join('> ' + l for l in qa(1, A, 'APROVADO', 90).splitlines())], 'AGUARDANDO_QA', A),
    ('modelo copiado com comentário na linha', [f"<!-- JA-QA\niteracao: 1\ncommit: {A}\nveredito: APROVADO        (ou REPROVADO)\nnota: 86\n-->",
                                                pg(1, A, 'CONCORDO')], 'LIBERAR', A),
    ('na 3, entregue, falta QA', [qa(1, A, 'REPROVADO', 50), pg(1, A, 'CONCORDO'), qa(2, B, 'REPROVADO', 55), pg(2, B, 'CONCORDO'), en(3, C)], 'AGUARDANDO_QA', C),
]
ok = falhas = 0
for nome, coms, status, commit in casos:
    r = decidir(coms)
    if r['status'] == status and (not commit or r['commit'] == commit.lower()):
        ok += 1
    else:
        falhas += 1; print('FALHOU:', nome, '->', r['status'], r['commit'], '| esperado', status, commit)
    markdown(r)  # não pode quebrar
assert blocos(qa(2, 'ABCDEF0', 'APROVADO', 150))[0]['nota'] == 100.0
print(f'{ok} ok, {falhas} falha(s)')
sys.exit(1 if falhas else 0)

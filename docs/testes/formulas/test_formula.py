import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from formula import calc, txt, Erro
C = {'Comprimento': 2.5, 'Altura': 2.1, 'Tipo': 'Porta 80x210', 'Nível': 'Térreo', 'Ambiente': 'Banho Suíte', 'Marca': 'P01', 'Vazio': '', 'Largura': 0.8, 'Área': 12.34}
casos = [
    ('{Comprimento} * {Altura}', 5.25),
    ('=ARRED({Comprimento} * {Altura}; 1)', 5.3),
    ('ARRED(2,345; 2)', 2.35),
    ('2,5 + 1', 3.5),
    ('SE({Altura} > 2; "alta"; "baixa")', 'alta'),
    ('SE({Altura} >= 2,1; "ok"; "não")', 'ok'),
    ('{Nível} & " - " & {Ambiente}', 'Térreo - Banho Suíte'),
    ('CONTÉM({Ambiente}; "banho")', True),
    ('CONTEM({Ambiente}; "BANHO") E {Largura} < 0,9', True),
    ('NÃO CONTÉM({Ambiente}; "cozinha")', True),
    ('{Largura} < 0,9 OU {Altura} > 3', True),
    ('E({Largura} < 0,7; {Altura} > 2)', False),
    ('ESCOLHER({Nível}; "Térreo"; "T"; "1º"; "P1"; "?")', 'T'),
    ('ESCOLHER({Nível}; "terreo"; "T"; "?")', 'T'),
    ('SES({Área} < 5; "P"; {Área} < 20; "M"; "G")', 'M'),
    ('DIVIDIR({Tipo}; " "; 2)', '80x210'),
    ('DIVIDIR({Tipo}; " "; -1)', '80x210'),
    ('ESQUERDA({Marca}; 1)', 'P'),
    ('DIREITA({Marca}; 2)', '01'),
    ('EXT.TEXTO({Tipo}; 7; 2)', '80'),
    ('NÚM.CARACT({Marca})', 3.0),
    ('MAIÚSCULA({Nível})', 'TÉRREO'),
    ('SUBSTITUIR({Tipo}; "porta"; "PT")', 'PT 80x210'),
    ('TEXTO({Comprimento}; 2) & " m"', '2,50 m'),
    ('TETO(2,31; 0,05)', 2.35),
    ('PISO(2,31; 0,05)', 2.30),
    ('ARREDONDAR.PARA.CIMA(2,301; 1)', 2.4),
    ('VAZIO({Vazio})', True),
    ('{Vazio} = ""', True),
    ('{Marca} = "p01"', True),
    ('{Marca} = 1', False),
    ('MÁX(1; 5; 3) - MÍN(4; 2)', 3.0),
    ('2 ^ 3 * 2', 16.0),
    ('-2 ^ 2', -4.0),
    ('MOD(7; 3)', 1.0),
    ('SOMA({Comprimento}; {Altura}; {Inexistente})', 4.6),
    ('{Inexistente} * 2', None),
    ('"a""b"', 'a"b'),
    ('ROUND({Comprimento}*{Altura}, 0)', 5.0),
    ('SE({Altura}>2;"A";"B")&"-"&{Marca}', 'A-P01'),
]
ok = 0
for e, esperado in casos:
    try: r = calc(e, C)
    except Erro as x: r = 'ERRO ' + str(x)
    good = (abs(r - esperado) < 1e-9) if isinstance(esperado, float) and isinstance(r, float) else r == esperado
    ok += good
    if not good: print('FALHOU', e, '→', repr(r), 'esperado', repr(esperado))
erros = ['SE({Altura} > 2; "a"', 'FOO(1)', '{Altura} +', 'Altura * 2', '"aberto']
for e in erros:
    try: calc(e, C); print('DEVIA DAR ERRO', e)
    except (Erro, KeyError, ValueError): ok += 1
print(f'{ok} de {len(casos) + len(erros)} ok')

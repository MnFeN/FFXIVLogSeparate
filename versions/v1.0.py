__author__ = 'MnFeN'
__version__ = '1.0'

import re
import time
import tkinter as tk
from tkinter import ttk
import tkinter.messagebox as msgbox   
from tkinter.filedialog import (askopenfilename, asksaveasfilename)
import ctypes

def getTimestamp(line):
    timeArray = time.strptime(line[3:].lstrip('|')[:19], "%Y-%m-%dT%H:%M:%S") # 开头日志行类型为2-3位
    timestamp = time.mktime(timeArray)
    return timestamp

def selectAll():
    isSelect = checkVars[0].get()
    for index in range(1,len(checkVars)):
        checkVars[index].set(isSelect)

def readLogFile():
    global filename, file, fights
    filename = askopenfilename(title="Select the log file", filetypes=[("日志文件", ".log")])
    file = open(filename, encoding='UTF-8')
    fights = []
    
    lineCount = 0
    selfID = ''
    mapName = ''

    def initialize():
        nonlocal selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount
        selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount = 0,0,0,0

        nonlocal startTimeStr, startTimestamp, startLine
        startTimeStr, startTimestamp, startLine = '00:00:00',0,0
    
    initialize()
    
    while 1:
        line = file.readline()
        if not line:
            break
        lineCount += 1

        #跳过无关行
        if line[0:3] not in ['21|','22|','01|','02|','25|','26|','33|']:
            continue

        #重置后第一次 1.{7} -> 4.{7} 的 Action：记录开始时间
        if startTimestamp == 0 and re.match(r'^2[12]\|.{34}1.{7}\|[^|]*\|[^|]*\|[^|]*\|4', line):
            startTimestamp = getTimestamp(line)
            startTimeStr = line[14:22]
            continue

        #map 01|time|mapID|mapName|...
        re_map = re.match(r'^01\|.{34}[^|]*\|(?P<mapName>[^|]*)\|', line)
        if re_map:
            mapName = re_map.group('mapName')
            startLine01 = lineCount
            continue

        #self 02|time|selfID|...
        re_self = re.match(r'^02\|.{34}(?P<selfID>.{8})\|', line)
        if re_self:
            selfID = re_self.group('selfID')
            initialize()                # 先进副本再启动 ACT 时不会有 Director 21 日志行，所以加上这行的初始化
            startLine = lineCount + 1   # 每次进本依次会产生 01, 40, 02 三行
            continue

        #death 25|time|pID|...
        re_death = re.match(r'^25\|.{34}(?P<pID>1.{7})\|', line)
        if re_death:
            if selfID == re_death.group('pID'):
                selfDeathCount += 1
            totalDeathCount += 1
            continue

        #status 26|time|statusID|statusName|statusTime|casterID|casterName|targetID|...
        re_dmgdown = re.match(r'^26\|.{34}[^|]*\|(伤害降低|Damage Down|ダメージ低下|Malus de dégâts|Schaden -)\|[^|]*\|[4E].{7}\|[^|]*\|(?P<targetID>1.{7})\|', line)
        if re_dmgdown:
            if selfID == re_dmgdown.group('targetID'):
                selfDmgdownCount += 1
            totalDmgdownCount += 1
            continue
        
        #director 33|time|.{8}|type|...
        re_director = re.match(r'^33\|.{34}.{8}\|400000(?P<type>..)\|', line)
        if re_director:
            if re_director.group('type') in ['01','06']:                                    # 01:start 06:restart
                initialize()
                startLine = lineCount #要不要+1？
            elif re_director.group('type') in ['03','11','12'] and startTimestamp != 0:     # 03:kill 11/12:wipe
                duration = int(getTimestamp(line) - startTimestamp)
                durationStr = '{:0>2d}\'{:0>2d}"'.format(int((duration-duration%60)/60), int(duration%60))  # time: 01'35"
                #[0:startLine 1:endLine 2:startTime 3:duration 4:isWipe 5:Map 6-7:selfDeath/Dmgdown 8-9:totalDeath/Dmgdown 10:startLine01]
                fights.append([
                    startLine, lineCount, startTimeStr, durationStr, re_director.group('type')[0]=='0', 
                    mapName, selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount, startLine01])
            continue
    
    #清空下方的表格Frame
    for widget in tableFrame.winfo_children():
        widget.destroy()

    global table, checkVars
    table=[[]]
    checkVars = []

    #标题行
    checkVars.append(tk.IntVar())
    table[0].append(ttk.Checkbutton(tableFrame, text='', variable=checkVars[0],command=selectAll))
    table[0].append(ttk.Label(tableFrame, text='No.'))
    table[0].append(ttk.Label(tableFrame, text='Start'))
    table[0].append(ttk.Label(tableFrame, text='Duration'))
    table[0].append(ttk.Label(tableFrame, text='Map'))
    table[0].append(ttk.Label(tableFrame, text='D'))
    table[0].append(ttk.Label(tableFrame, text='D -'))
    for i in range(len(table[0])):
        table[0][i].grid(row=0,column=i,padx=10,pady=5)
    #下面每行
    for i in range(len(fights)):
        table.append([])
        isWipeColor = ['#d16969','#4ec9b0'][fights[i][4]] # wipe=red, kill=green
        checkVars.append(tk.IntVar())
        table[-1].append(ttk.Checkbutton(tableFrame, text='', variable=checkVars[-1]))         #table[row][0]: checkbox
        table[-1].append(ttk.Label(tableFrame,text=str(i+1),foreground=isWipeColor))           #table[row][1]: index
        table[-1].append(ttk.Label(tableFrame,text=str(fights[i][2]),foreground=isWipeColor))  #table[row][2]: startTime
        table[-1].append(ttk.Label(tableFrame,text=str(fights[i][3]),foreground=isWipeColor))  #table[row][3]: duration
        table[-1].append(ttk.Label(tableFrame,text=str(fights[i][5])))                         #table[row][4]: map
        table[-1].append(ttk.Label(tableFrame,text=str(fights[i][6])+'/'+str(fights[i][8])))   #table[row][5]: d
        table[-1].append(ttk.Label(tableFrame,text=str(fights[i][7])+'/'+str(fights[i][9])))   #table[row][6]: d-
        for col in range(len(table[-1])):
            table[-1][col].grid(row=i+1,column=col,padx=10,pady=5)

def saveLogFile():
    fightIndexes = []
    fightRawIndexes = re.split(',|，', input('Input all indexes of fights you want to extract. e.g. 0-4,7, 10 - 15 , 20\n'))
    for i in range(len(fightRawIndexes)):
        fightRawIndexes[i] = fightRawIndexes[i].replace(' ','')
        re_fightIndex = re.match(r'(\d+)(?:[-–−—~](\d+))?',fightRawIndexes[i])
        print(re_fightIndex.group(1),re_fightIndex.group(2))
        if re_fightIndex:
            if re_fightIndex.group(2) is None:
                fightIndexes.append(int(fightRawIndexes[i]))
            else:
                for j in range(int(re_fightIndex.group(1)),int(re_fightIndex.group(2))+1):
                    fightIndexes.append(j)
        else:
            print(fightRawIndexes[i] + ' is not a number or a range.')

    onLines = []
    offLines = []
    for i in range(len(fights)):
        if i not in fightIndexes:
            offLines.append(fights[i][0])
            onLines.append(fights[i][1])

    file.seek(0,0)
    output_file = open(filename[:-4]+'_extract.log','w', encoding='UTF-8')
    lineCount = 0
    switch = 1

    while 1:
        line = file.readline()
        if not line:
            break
        lineCount += 1
        if lineCount in offLines:
            switch = 0
        if switch == 1:
            output_file.write(line)
        if switch == 0:
            output_file.write('00|' + line[3:].lstrip('|')[:35] + '0038||trash fight|0000000000000000\n')
        if lineCount in onLines:
            switch = 1
    file.close()
    output_file.close()

ctypes.windll.shcore.SetProcessDpiAwareness(1)                  #使用程序自身的dpi适配
ScaleFactor=ctypes.windll.shcore.GetScaleFactorForDevice(0)     #获取屏幕的缩放因子

window = tk.Tk()
window.tk.call('tk', 'scaling', ScaleFactor/100)                #设置程序缩放
#默认全屏幕 70% 尺寸居中
screen_x = window.winfo_screenwidth()
screen_y = window.winfo_screenheight()
window.geometry('%dx%d+%d+%d' % (int(screen_x*0.7), int(screen_y*0.7), int(screen_x*0.15), int(screen_y*0.12)))

button_import = ttk.Button(window, text='Import Log', command=readLogFile)
button_export = ttk.Button(window, text='Export Log', command=saveLogFile)
button_import.place(relx=0.5-0.15,relwidth=0.15,rely=0.05,relheight=0.06,anchor='center')
button_export.place(relx=0.5+0.15,relwidth=0.15,rely=0.05,relheight=0.06,anchor='center')

tableFrame = ttk.Frame(window, borderwidth=1)
tableFrame.place(relx=0.5,rely=0.55,anchor='center')

window.mainloop()

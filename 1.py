import re
import time

filename = input('Enter the log file name in the same folder\n')
if not re.search(r'\.[Ll][Oo][Gg] *$', filename):
    filename = filename + '.log'
file = open(filename)
lineCount = 0
zoneName = ''
mapName = ''
selfID = ''

def initialize():
    global selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount, startTimeMMSS, startTimestamp, startLine
    selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount, startTimeMMSS, startTimestamp, startLine = 0,0,0,0,'00:00',0,0
    
def getTimestamp(line):
    timeArray = time.strptime(line[3:22], "%Y-%m-%dT%H:%M:%S")
    timestamp = time.mktime(timeArray)
    return timestamp

fights = []
initialize()

while 1:
    line = file.readline()
    if not line:
        break
    lineCount += 1

    #跳过无关行
    if line[0:3] not in ['21|','22|','01|','40|','02|','25|','26|','33|']:
        continue

    #重置后第一次 1.{7} -> 4.{7} 的 Action：记录开始时间
    if startTimestamp == 0 and re.match(r'^2[12]\|.{34}1.{7}\|[^|]*\|[^|]*\|[^|]*\|4', line):
        startTimestamp = getTimestamp(line)
        startTimeMMSS = line[14:19]
        continue

    #map 01|time|mapID|mapName|...
    re_map = re.match(r'^01\|.{34}[^|]*\|(?P<mapName>[^|]*)\|', line)
    if re_map:
        mapName = re_map.group('mapName')
        initialize() #【检查：后启动时有没有21行】
        startLine = lineCount + 3 #跳过01,40,02三行
        continue
    
    #zone 40|time|zoneID|regionName|zoneName|...
    re_zone = re.match(r'^40\|.{34}[^|]*\|[^|]*\|(?P<zoneName>[^|]*)\|', line)
    if re_zone:
        zoneName = re_zone.group('zoneName')
        continue

    #self 02|time|selfID|...
    re_self = re.match(r'^02\|.{34}(?P<selfID>.{8})\|', line)
    if re_self:
        selfID = re_self.group('selfID')
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
    
    #control 33|time|.{8}|type|...
    re_control = re.match(r'^33\|.{34}.{8}\|400000(?P<type>..)\|', line)
    if re_control:
        if re_control.group('type') in ['01','06']:
            initialize()
            startLine = lineCount #要不要+1？
        elif re_control.group('type') in ['03','10','11'] and startTimestamp != 0: #03:kill 10/11:wipe
            duration = int(getTimestamp(line) - startTimestamp)
            durationMMSS = str(int((duration-duration%60)/60)) + ' m ' + str(duration%60) + ' s'
            #[0:startLine 1:endLine 2:startTime 3:duration 4:isWipe 5:Map 6:Zone 7-8:selfDeath/Dmgdown 9-10:totalDeath/Dmgdown]
            fights.append([startLine, lineCount, startTimeMMSS, durationMMSS, re_control.group('type')[0]=='0', mapName, zoneName, selfDeathCount, selfDmgdownCount, totalDeathCount, totalDmgdownCount])
        continue

for i in range(len(fights)):
    print(i, fights[i][0], fights[i][1], fights[i][2], fights[i][4], fights[i][6], fights[i][5], fights[i][3], fights[i][7], '/', fights[i][9], fights[i][8], '/', fights[i][10])

fightIndexes = []
fightRawIndexes = re.split(',|，', input('Input all indexes of fights you want to extract. e.g. 0-4,7, 10 - 15 , 20\n'))
for i in range(len(fightRawIndexes)):
    fightRawIndexes[i] = fightRawIndexes[i].replace(' ','')
    re_fightIndex = re.match(r'(\d)+(?:[-–−—~](\d)+)?',fightRawIndexes[i])
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
output_file = open(filename[:-4]+'_extract.log','w')
lineCount = 0
switch = 1
print(onLines)
print(offLines)
while 1:
    line = file.readline()
    if not line:
        break
    lineCount += 1
    if lineCount in offLines:
        switch = 0
    elif lineCount in onLines:
        switch = 1
    if switch == 1:
        output_file.write(line)
        #print(lineCount)
file.close()
output_file.close()
input('Logs extracted. Press enter to exit.\n')

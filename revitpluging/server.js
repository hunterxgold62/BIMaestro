const express = require('express');
const bodyParser = require('body-parser');
const ExcelJS = require('exceljs');

const app = express();
app.use(bodyParser.json());

const workbook = new ExcelJS.Workbook();
const worksheet = workbook.addWorksheet('User Info');

worksheet.columns = [
    { header: 'Username', key: 'username', width: 30 },
    { header: 'Email', key: 'email', width: 30 },
    { header: 'Timestamp', key: 'timestamp', width: 30 },
    { header: 'Project Name', key: 'projectname', width: 30 },
    { header: 'Revit Version', key: 'revitversion', width: 30 },
    { header: 'Computer Name', key: 'computername', width: 30 },
    { header: 'Windows Username', key: 'windowsusername', width: 30 },
    { header: 'Action', key: 'action', width: 10 }, // "open" or "close"
    { header: 'Session Duration', key: 'sessionduration', width: 15 }
];

app.post('/api/userinfo', (req, res) => {
    console.log('Received POST request to /api/userinfo');
    const { username, email, timestamp, projectname, revitversion, computername, windowsusername, action, sessionduration } = req.body;

    console.log(`Data received: ${JSON.stringify(req.body)}`);

    worksheet.addRow({ username, email, timestamp, projectname, revitversion, computername, windowsusername, action, sessionduration });

    workbook.xlsx.writeFile('UserInfo.xlsx')
        .then(() => {
            console.log('Excel file written successfully');
            res.sendStatus(200);
        })
        .catch(err => {
            console.error('Error writing to Excel file:', err);
            res.sendStatus(500);
        });
});

app.listen(8080, () => { // Changez ici pour le port 8080
    console.log('Server is running on port 8080');
});
